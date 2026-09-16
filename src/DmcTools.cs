using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DmcMcp;

/// <summary>
/// Инструменты публикации поста в DMC из редактора. Публикация идёт через bulk-zip бэкенда: он сам
/// разбирает архив и дописывает поля ИИ, поэтому агент не заполняет карточку руками. Все ответы —
/// текст для человека; ошибка начинается с «ОШИБКА:», исключения наружу не выходят.
/// </summary>
[McpServerToolType]
public class DmcTools(IDmcClient dmc, DmcTokenProvider tokens)
{
    /** Подменяются в тестах: настоящий сон в опросе там ни к чему. */
    internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;
    internal TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    /** Значения MachineType бэкенда (model/MachineType.java, 2026-09-16). */
    internal static readonly string[] MachineTypes =
    {
        "MILLING", "TURNING", "MILL_TURN", "WIRE_EDM", "LASER", "PLASMA", "WATERJET", "GRINDING",
        "ROBOT", "EDM", "ROUTER", "SWISS", "GAS_PLASMA_LASER", "ADDITIVE", "OTHER",
    };

    internal const string NoLogin =
        "ОШИБКА: входа в DMC на этой машине нет — выполните `" + Brand.Cli + " login` один раз в терминале.";

    // ------------------------------------------------------------- check_post_status

    [McpServerTool(Name = "check_post_status"), Description(
        "Что DMC знает о посте: статус (черновик / на модерации / опубликован), ссылка на карточку, " +
        "стойка и станок. Принимает id или slug.")]
    public async Task<string> CheckPostStatus(
        [Description("id или slug поста")] string idOrSlug)
    {
        var (token, _) = await Token(); // без входа тоже работает: опубликованное видно всем
        ProductInfo? p;
        try { p = await dmc.GetProduct(idOrSlug, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null)
            return $"DMC не нашёл «{idOrSlug}» — нет такого, или это чужой черновик"
                   + (token == null ? $" (входа нет: выполните `{Brand.Cli} login`, чтобы видеть свои черновики)." : ".");
        return Describe(p);
    }

    // ------------------------------------------------------------------ publish_post

    /** Что bulk-zip бэкенда принимает как одиночный пост (BulkZipImportService, 2026-09-16). */
    internal static readonly string[] PostExtensions = { ".sppx", ".dll", ".stnci", ".zip" };

    [McpServerTool(Name = "publish_post"), Description(
        "Загрузить постпроцессор в Digital Machine Center черновиком: бэкенд разбирает архив, ИИ дописывает " +
        "описание, стойку, станок и обложку. На модерацию НЕ отправляет — проверьте черновик " +
        "(update_post поправит поля) и вызовите submit_post.")]
    public async Task<string> PublishPost(
        [Description("Путь к файлу поста: .sppx, .dll, .stnci или .zip")] string file,
        [Description("Подсказка имени компонента, например «Fanuc 0i-MF для Haas VF-2»")] string? name = null,
        [Description("Подсказка для описания: особенности поста, для какого станка")] string? descriptionHint = null,
        [Description("true (по умолчанию) — ИИ дописывает описание, обложку и метаданные; false — только разбор архива")] bool ai = true)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ОШИБКА: файла {path} нет.";
        if (!PostExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"ОШИБКА: {Path.GetFileName(path)} — не пост. Нужен .sppx, .dll, .stnci или .zip.";

        var (token, err) = await Token();
        if (token == null) return err!;

        string importId = Guid.NewGuid().ToString("N");
        try { importId = await dmc.StartImport(path, importId, ai, name, descriptionHint, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: не удалось отправить файл в DMC: " + e.Message; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ImportProgress progress;
        while (true)
        {
            try { progress = await dmc.GetImportProgress(importId, token); }
            catch (DmcHttpException e) { return Explain(e); }
            catch (Exception e) { return $"ОШИБКА: DMC не ответил про импорт {importId}: {e.Message}"; }
            if (progress.Finished) break;
            // «queued» — очередь за чужим импортом, не зависание; ждём так же.
            if (sw.Elapsed >= MaxWait)
                return $"Импорт {importId} всё ещё идёт (сейчас: {progress.Status}, {progress.CurrentName}). "
                     + "Сервер продолжит сам — черновик появится в кабинете DMC в «My components». "
                     + "Повторно файл не отправляйте.";
            await Delay(PollEvery);
        }

        if (progress.Status == "error") return "ОШИБКА импорта: " + (progress.StatusReason ?? "причина не названа");
        if (progress.Status == "cancelled") return "Импорт отменён на сервере.";
        if (progress.Components.Count == 0)
            return "ОШИБКА: DMC не нашёл в файле компонента."
                 + (progress.Errors.Count > 0 ? "\n" + string.Join("\n", progress.Errors) : "");

        var sb = new StringBuilder();
        foreach (var c in progress.Components)
        {
            sb.AppendLine($"Создан черновик: {c.Name} ({c.ContentType})" + (c.AiEnriched ? ", поля дописал ИИ" : ""));
            ProductInfo? p = null;
            try { p = await dmc.GetProduct(c.ProductId, token); }
            catch (Exception) { /* карточка не прочиталась — ниже отдадим хотя бы id */ }
            if (p != null)
            {
                sb.AppendLine(Describe(p));
                sb.AppendLine(p.HasCover ? "Обложка: есть" : "Обложка: нет");
                sb.AppendLine(string.IsNullOrWhiteSpace(p.Description) ? "Описание: нет" : "Описание: есть");
            }
            sb.AppendLine($"id: {c.ProductId}");
        }
        foreach (var e in progress.Errors) sb.AppendLine("Не принято: " + e);
        sb.AppendLine("Проверьте стойку, станок и описание (update_post поправит), затем submit_post — отправить на модерацию.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------- update_post

    [McpServerTool(Name = "update_post"), Description(
        "Поправить поля черновика, которые ИИ угадал неверно: имя, описание, стойка, станок, тип станка, " +
        "число осей. Файлы, цену и статус не трогает. Передавайте только то, что меняете.")]
    public async Task<string> UpdatePost(
        [Description("id поста")] string id,
        [Description("Имя компонента")] string? name = null,
        [Description("Описание")] string? description = null,
        [Description("Производитель стойки, например Fanuc")] string? controllerManufacturer = null,
        [Description("Серия стойки, например 0i")] string? controllerSeries = null,
        [Description("Модель стойки, например MF")] string? controllerModel = null,
        [Description("Производитель станка, например Haas")] string? machineManufacturer = null,
        [Description("Серия станка, например VF")] string? machineSeries = null,
        [Description("Модель станка, например VF-2")] string? machineModel = null,
        [Description("Тип станка: MILLING, TURNING, MILL_TURN, WIRE_EDM, LASER, PLASMA, WATERJET, GRINDING, " +
                     "ROBOT, EDM, ROUTER, SWISS, GAS_PLASMA_LASER, ADDITIVE, OTHER")] string? machineType = null,
        [Description("Число осей, от 1 до 12")] int? numberOfAxes = null)
    {
        // Проверки до похода в DMC: опечатку в типе станка автор узнаёт сразу, а не после PUT.
        if (machineType != null)
        {
            machineType = machineType.Trim().ToUpperInvariant();
            if (!MachineTypes.Contains(machineType))
                return $"ОШИБКА: тип станка «{machineType}» неизвестен. Есть: " + string.Join(", ", MachineTypes);
        }
        if (numberOfAxes is < 1 or > 12) return "ОШИБКА: число осей — от 1 до 12.";

        var (token, err) = await Token();
        if (token == null) return err!;

        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));

        // PUT у DMC полный: тело — вся текущая карточка, поверх неё только переданное.
        var body = DmcJson.RequestFrom(p.Raw);
        var changes = new List<string>();
        void Set(string field, string? old, string? value)
        {
            if (value == null || value == old) return;
            body[field] = value;
            changes.Add($"{field}: {old ?? "—"} → {value}");
        }
        Set("name", p.Name, name);
        Set("description", p.Description, description);
        Set("controllerManufacturer", p.ControllerManufacturer, controllerManufacturer);
        Set("controllerSeries", p.ControllerSeries, controllerSeries);
        Set("controllerModel", p.ControllerModel, controllerModel);
        Set("machineManufacturer", p.MachineManufacturer, machineManufacturer);
        Set("machineSeries", p.MachineSeries, machineSeries);
        Set("machineModel", p.MachineModel, machineModel);
        Set("machineType", p.MachineType, machineType);
        if (numberOfAxes is int n && n != p.NumberOfAxes)
        {
            body["numberOfAxes"] = n;
            changes.Add($"numberOfAxes: {p.NumberOfAxes?.ToString() ?? "—"} → {n}");
        }
        if (changes.Count == 0) return "Менять нечего — все переданные значения уже такие.";

        try { await dmc.UpdateProduct(id, body, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        return "Изменено:\n" + string.Join("\n", changes) + "\nСсылка: " + p.Url(dmc.Site);
    }

    // ------------------------------------------------------------------- submit_post

    [McpServerTool(Name = "submit_post"), Description(
        "Отправить черновик на модерацию. DMC требует имя, производителя станка, тип станка и архив — " +
        "если чего-то нет, скажет; заполните через update_post и повторите.")]
    public async Task<string> SubmitPost(
        [Description("id поста")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;

        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));
        if (p.PublicationStatus is "PENDING_REVIEW" or "PUBLISHED")
            return $"{p.Name} уже {StatusWord(p.PublicationStatus)} — ничего не менял.\nСсылка: {p.Url(dmc.Site)}";

        try { p = await dmc.SetStatus(id, "PENDING_REVIEW", token); }
        catch (DmcHttpException e) when (e.Status == 400)
        {
            return "ОШИБКА: DMC не принял на модерацию — " + ErrorText(e.Body)
                 + ". Заполните недостающее через update_post и повторите.";
        }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }

        return $"{p.Name} отправлен на модерацию — в каталоге появится после одобрения.\nСсылка: {p.Url(dmc.Site)}";
    }

    // ------------------------------------------------------------------- общее

    internal string Describe(ProductInfo p)
    {
        var line = $"{p.Name} — {StatusWord(p.PublicationStatus)}\nСсылка: {p.Url(dmc.Site)}\n"
                 + $"Стойка: {p.Controller}\nСтанок: {p.Machine}";
        if (p.MachineType != null) line += $", тип {p.MachineType}";
        if (p.NumberOfAxes is int n) line += $", осей {n}";
        return line;
    }

    internal static string StatusWord(string s) => s switch
    {
        "DRAFT" => "черновик",
        "PENDING_REVIEW" => "на модерации",
        "PUBLISHED" => "опубликован",
        "REJECTED" => "отклонён",
        "DISABLED" => "отключён",
        "ARCHIVED" => "в архиве",
        _ => s,
    };

    internal static string Explain(DmcHttpException e) => e.Status switch
    {
        401 => NoLogin,
        403 => "ОШИБКА: нужна роль паблишера в DMC — попросите её у администратора.",
        404 => "ОШИБКА: DMC не нашёл такой компонент — или он не ваш.",
        409 => "ОШИБКА: у вас уже идёт импорт в DMC — дождитесь его и повторите. (" + ErrorText(e.Body) + ")",
        _ => $"ОШИБКА: DMC ответил {e.Status}: {ErrorText(e.Body)}",
    };

    /** Бэкенд шлёт ошибки как {"error":"..."} — показываем текст, а не JSON. */
    internal static string ErrorText(string body)
    {
        try
        {
            var r = JsonDocument.Parse(body).RootElement;
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString()!;
        }
        catch (JsonException) { /* не JSON — отдаём как есть */ }
        return body.Length > 300 ? body[..300] : body;
    }

    private async Task<(string? Token, string? Error)> Token()
    {
        try
        {
            var t = await tokens.GetAccessToken();
            return (t, t == null ? NoLogin : null);
        }
        catch (InvalidOperationException e) { return (null, "ОШИБКА: " + e.Message); }
    }
}
