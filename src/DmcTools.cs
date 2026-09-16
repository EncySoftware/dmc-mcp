using System.ComponentModel;
using System.IO.Compression;
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

    /** Что bulk-zip бэкенда принимает как одиночный пост (BulkZipImportService, 2026-09-16). */
    internal static readonly string[] PostExtensions = { ".sppx", ".dll", ".stnci", ".zip" };

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

        var (progress, importErr) = await Import(path, ai, name, descriptionHint, token);
        if (progress == null) return importErr!;

        var sb = new StringBuilder();
        await ReportComponents(sb, progress, token);
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
        var fields = new FieldSet(name, description, controllerManufacturer, controllerSeries, controllerModel,
            machineManufacturer, machineSeries, machineModel, machineType, numberOfAxes);
        var (normalized, fieldErr) = Normalize(fields);
        if (normalized == null) return fieldErr!;

        var (token, err) = await Token();
        if (token == null) return err!;

        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));

        var (changes, putErr) = await PutFields(p, normalized, token);
        if (putErr != null) return putErr;
        if (changes.Count == 0) return "Менять нечего — все переданные значения уже такие.";
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

    // -------------------------------------------------------------------- find_posts

    [McpServerTool(Name = "find_posts"), Description(
        "Найти посты до заливки, чтобы не плодить дубли: опубликованные в каталоге и ваши собственные " +
        "(черновики тоже). Хотя бы один критерий: текст (имя, модель станка, слово из описания), " +
        "производитель стойки, производитель станка.")]
    public async Task<string> FindPosts(
        [Description("Текст: имя поста, модель станка, слово из описания")] string? query = null,
        [Description("Производитель стойки, например Fanuc")] string? controllerManufacturer = null,
        [Description("Производитель станка, например Haas")] string? machineManufacturer = null,
        [Description("Тип: POST_PROCESSOR (по умолчанию), MACHINE_SCHEMA, INTERPRETER, DIGITAL_MACHINE_KIT или ANY — все")] string? contentType = "POST_PROCESSOR")
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(controllerManufacturer)
            && string.IsNullOrWhiteSpace(machineManufacturer))
            return "ОШИБКА: укажите хотя бы что-то — текст, стойку или производителя станка.";
        var (type, typeErr) = NormalizeType(contentType);
        if (typeErr != null) return typeErr;

        var (token, _) = await Token(); // каталог виден и без входа; свои черновики — только с ним
        IReadOnlyList<ProductInfo> published;
        try { published = await dmc.Search(type, query, controllerManufacturer, machineManufacturer, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }

        var mine = new List<ProductInfo>();
        if (token != null)
        {
            try
            {
                mine = (await dmc.MyProducts(token))
                    .Where(p => (type == null || p.ContentType == type) && Matches(p, query, controllerManufacturer, machineManufacturer))
                    .ToList();
            }
            catch (Exception) { /* свои не прочитались — покажем хотя бы каталог */ }
        }

        var mineIds = mine.Select(p => p.Id).ToHashSet();
        var sb = new StringBuilder();
        foreach (var p in mine) sb.AppendLine(Line(p, own: true));
        foreach (var p in published) if (!mineIds.Contains(p.Id)) sb.AppendLine(Line(p, own: false));
        if (sb.Length == 0) sb.AppendLine("DMC ничего не нашёл — можно публиковать.");
        if (token == null) sb.AppendLine($"Ваши черновики не проверены — входа нет (`{Brand.Cli} login`).");
        return sb.ToString();
    }

    private string Line(ProductInfo p, bool own) =>
        $"{(own ? "[ваш] " : "")}{(p.ContentType == "POST_PROCESSOR" ? "" : $"[{p.ContentType}] ")}{p.Name} — "
        + $"{StatusWord(p.PublicationStatus)} — стойка {p.Controller} — станок {p.Machine} — {p.Url(dmc.Site)} — id {p.Id}";

    /** Типы компонентов бэкенда (model/ContentType.java). */
    internal static readonly string[] ContentTypes = { "POST_PROCESSOR", "MACHINE_SCHEMA", "INTERPRETER", "DIGITAL_MACHINE_KIT" };

    /** Тип из параметра: null — любой (ANY, ALL или пусто); иначе одно из ContentTypes. Ошибка — готовым текстом. */
    private static (string? Type, string? Error) NormalizeType(string? contentType)
    {
        var t = (contentType ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0 || t == "ANY" || t == "ALL") return (null, null);
        if (!ContentTypes.Contains(t))
            return (null, $"ОШИБКА: тип «{t}» неизвестен. Есть: " + string.Join(", ", ContentTypes) + ", ANY");
        return (t, null);
    }

    /** Свои приходят все — фильтр на месте, по тем же полям, по которым каталог ищет query. */
    private static bool Matches(ProductInfo p, string? query, string? controller, string? maker)
    {
        static bool Has(string? hay, string? needle) =>
            string.IsNullOrWhiteSpace(needle) || (hay ?? "").Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
        bool q = string.IsNullOrWhiteSpace(query)
                 || Has(p.Name, query) || Has(p.Description, query) || Has(p.Controller, query) || Has(p.Machine, query);
        return q && Has(p.ControllerManufacturer, controller) && Has(p.MachineManufacturer, maker);
    }

    // ------------------------------------------------------------ replace_post_file

    [McpServerTool(Name = "replace_post_file"), Description(
        "Заменить архив существующего поста новой версией: файл уходит в хранилище, карточка обновляется, " +
        "старый архив удаляется. У опубликованного поста новый архив попадает в каталог сразу, без " +
        "повторной модерации.")]
    public async Task<string> ReplacePostFile(
        [Description("id поста")] string id,
        [Description("Путь к новому файлу: .sppx, .dll, .stnci или .zip")] string file)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ОШИБКА: файла {path} нет.";
        if (!PostExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"ОШИБКА: {Path.GetFileName(path)} — не пост. Нужен .sppx, .dll, .stnci или .zip.";

        var (token, err) = await Token();
        if (token == null) return err!;

        // Карточку читаем ДО загрузки: чужой или несуществующий id не должен стоить файла в tmp/.
        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));

        string staged;
        try { staged = await dmc.UploadFile(path, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: не удалось загрузить файл в DMC: " + e.Message; }

        // Полный PUT с путём tmp/…: бэкенд переносит файл к продукту, удаляет старый, перечитывает
        // габариты и обновляет лицензионный контейнер у тех, кто пост уже держит.
        var body = DmcJson.RequestFrom(p.Raw);
        body["productFile"] = staged;
        try { await dmc.UpdateProduct(p.Id, body, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }

        var sb = new StringBuilder();
        sb.AppendLine($"Архив поста «{p.Name}» заменён на {Path.GetFileName(path)}; старый удалён, копии у держателей лицензий обновлены.");
        if (p.PublicationStatus == "PUBLISHED")
            sb.AppendLine("Пост опубликован — новый архив уходит в каталог сразу, без повторной модерации.");
        sb.AppendLine("Ссылка: " + p.Url(dmc.Site));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- publish_folder

    /** Лимит одной загрузки в bulk-zip на бэкенде (spring.servlet.multipart, 1 ГБ). */
    internal const long MaxUploadBytes = 1024L * 1024 * 1024;

    [McpServerTool(Name = "publish_folder"), Description(
        "Загрузить все посты из папки одним импортом — каждый становится отдельным черновиком. " +
        "Manifest (CSV: file,name,controllerManufacturer,…) даёт точные имя и поля вместо догадок ИИ. " +
        "На модерацию не отправляет.")]
    public async Task<string> PublishFolder(
        [Description("Папка с компонентами: файлы постов (.sppx, .dll, .stnci, .zip) и подпапки — схема (xml + osd) или кит папкой")] string dir,
        [Description("Путь к CSV-манифесту: колонки file, name, description, controllerManufacturer, " +
                     "controllerSeries, controllerModel, machineManufacturer, machineSeries, machineModel, " +
                     "machineType, numberOfAxes; обязательна только file")] string? manifest = null,
        [Description("true (по умолчанию) — ИИ дописывает описание, обложку и метаданные; false — только разбор архива")] bool ai = true)
    {
        var dirPath = Path.GetFullPath(dir);
        if (!Directory.Exists(dirPath)) return $"ОШИБКА: папки {dirPath} нет.";
        var files = Directory.GetFiles(dirPath)
            .Where(f => PostExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Подпапка — тоже компонент: схема (xml + osd) или кит едут папкой, bulk-zip разбирает их сам.
        var dirs = Directory.GetDirectories(dirPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0 && dirs.Count == 0)
            return $"ОШИБКА: в {dirPath} нет файлов постов (.sppx, .dll, .stnci, .zip) и нет подпапок.";

        Manifest? man = null;
        var notes = new List<string>();
        if (manifest != null)
        {
            var mp = Path.GetFullPath(manifest);
            if (!File.Exists(mp)) return $"ОШИБКА: manifest {mp} не найден.";
            try { man = Manifest.Parse(mp); }
            catch (InvalidDataException e) { return "ОШИБКА: manifest — " + e.Message; }
            var present = files.Concat(dirs).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in man.Files)
                if (!present.Contains(f)) notes.Add($"В manifest есть {f}, а его нет в папке — строка пропущена.");
        }

        var (token, err) = await Token();
        if (token == null) return err!;

        // Папка на компонент: так bulk-zip делит архив на отдельные черновики и берёт имя из папки.
        var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // папка → файл
        var zipPath = Path.Combine(Path.GetTempPath(), "dmc-folder-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    var fileName = Path.GetFileName(f);
                    var wanted = man?.For(fileName)?.Name;
                    var folder = SafeFolderName(string.IsNullOrWhiteSpace(wanted) ? Path.GetFileNameWithoutExtension(f) : wanted!);
                    var unique = folder;
                    for (int n = 2; folderOf.ContainsKey(unique); n++) unique = $"{folder} ({n})";
                    folderOf[unique] = fileName;
                    zip.CreateEntryFromFile(f, unique + "/" + fileName);
                }
                foreach (var d in dirs)
                {
                    var dirName = Path.GetFileName(d);
                    var wanted = man?.For(dirName)?.Name;
                    var folder = SafeFolderName(string.IsNullOrWhiteSpace(wanted) ? dirName : wanted!);
                    var unique = folder;
                    for (int n = 2; folderOf.ContainsKey(unique); n++) unique = $"{folder} ({n})";
                    folderOf[unique] = dirName;
                    foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, unique + "/" + Path.GetRelativePath(d, f).Replace('\\', '/'));
                }
            }
            if (new FileInfo(zipPath).Length > MaxUploadBytes)
                return "ОШИБКА: архив получился больше 1 ГБ — разбейте папку на части.";

            var (progress, importErr) = await Import(zipPath, ai, null, null, token);
            if (progress == null) return importErr!;

            var sb = new StringBuilder();
            foreach (var note in notes) sb.AppendLine(note);
            await ReportComponents(sb, progress, token);
            if (man != null) await ApplyManifest(sb, progress, man, folderOf, token);
            sb.AppendLine($"Итого {progress.Components.Count} черновик(ов). Проверьте и вызовите submit_post для каждого.");
            return sb.ToString();
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* временный файл */ }
        }
    }

    /** Имя папки в zip из имени компонента: символы, запрещённые в путях, — в дефис. */
    internal static string SafeFolderName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => bad.Contains(ch) || ch == '/' || ch == '\\' ? '-' : ch).ToArray())
            .Trim().TrimEnd('.');
        return s.Length == 0 ? "post" : s;
    }

    /** Имя, каким bulk-zip вернёт папку: он лишь заменяет «_» между цифрами на «/». */
    private static string MatchKey(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "(?<=\\d)_(?=\\d)", "/").Trim();

    /** Поля из manifest — PUT-ом на каждый созданный черновик; каждая неудача — строкой в отчёте. */
    private async Task ApplyManifest(StringBuilder sb, ImportProgress progress, Manifest man,
        Dictionary<string, string> folderOf, string token)
    {
        foreach (var c in progress.Components)
        {
            var pair = folderOf.FirstOrDefault(kv =>
                string.Equals(MatchKey(kv.Key), c.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pair.Key == null) continue;
            var entry = man.For(pair.Value);
            if (entry == null) continue;
            var fields = entry.Fields with { Name = entry.Name };
            if (fields.IsEmpty) continue;

            var (normalized, fieldErr) = Normalize(fields);
            if (normalized == null) { sb.AppendLine($"{c.Name}: manifest не применён — {fieldErr}"); continue; }
            ProductInfo? p = null;
            try { p = await dmc.GetProduct(c.ProductId, token); }
            catch (Exception) { /* ниже — строкой в отчёте */ }
            if (p == null) { sb.AppendLine($"{c.Name}: manifest не применён — карточка не прочиталась."); continue; }
            var (changes, putErr) = await PutFields(p, normalized, token);
            if (putErr != null) { sb.AppendLine($"{c.Name}: manifest не применён — {putErr}"); continue; }
            if (changes.Count > 0) sb.AppendLine($"{c.Name}: из manifest — " + string.Join("; ", changes));
        }
    }

    // ---------------------------------------------------------------- search_schemas

    [McpServerTool(Name = "search_schemas"), Description(
        "Найти схемы станков в каталоге — чтобы привязать к ним пост (link_post_to_machines). " +
        "Хотя бы один критерий: текст (модель или имя станка) или производитель станка.")]
    public async Task<string> SearchSchemas(
        [Description("Текст: модель или имя станка, например VF-2")] string? query = null,
        [Description("Производитель станка, например Haas")] string? machineManufacturer = null)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(machineManufacturer))
            return "ОШИБКА: укажите текст или производителя станка.";
        var (token, _) = await Token();
        IReadOnlyList<ProductInfo> found;
        try { found = await dmc.Search("MACHINE_SCHEMA", query, null, machineManufacturer, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (found.Count == 0) return "DMC не нашёл схем по этому запросу.";
        var sb = new StringBuilder();
        foreach (var p in found)
            sb.AppendLine($"{p.Name} — станок {p.Machine}"
                          + (p.MachineType != null ? $", тип {p.MachineType}" : "")
                          + (p.NumberOfAxes is int n ? $", осей {n}" : "")
                          + $" — {p.Url(dmc.Site)} — id {p.Id}");
        return sb.ToString();
    }

    // -------------------------------------------------------- link_post_to_machines

    [McpServerTool(Name = "link_post_to_machines"), Description(
        "Привязать пост к схемам станков, для которых он сделан (связь MADE_FOR): на карточках появятся " +
        "«сделан для» и «рекомендуемые посты». id схем — из search_schemas.")]
    public async Task<string> LinkPostToMachines(
        [Description("id поста")] string id,
        [Description("id схем через запятую или пробел")] string schemaIds)
    {
        var ids = schemaIds
            .Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();
        if (ids.Count == 0) return "ОШИБКА: не передано ни одного id схемы.";

        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        // MADE_FOR идёт только от поста: схему или кит бэкенд отвергнет — говорим раньше него.
        if (p.ContentType != "POST_PROCESSOR")
            return $"ОШИБКА: «{p.Name}» — не пост ({p.ContentType}); связь «сделан для» идёт только от поста к схеме.";

        try { await dmc.AddLinks(p.Id, "MADE_FOR", ids, token); }
        catch (DmcHttpException e) when (e.Status == 400) { return "ОШИБКА: DMC отверг связь — " + ErrorText(e.Body); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }

        IReadOnlyList<LinkInfo> links;
        try { links = await dmc.GetLinks(p.Id, token); }
        catch (Exception) { links = Array.Empty<LinkInfo>(); }
        var made = links.Where(l => l.LinkType == "MADE_FOR" && l.Product != null).Select(l => l.Product!.Name).ToList();
        return $"Пост «{p.Name}» — сделан для: " + (made.Count > 0 ? string.Join(", ", made) : string.Join(", ", ids))
             + "\nСсылка: " + p.Url(dmc.Site);
    }

    // ----------------------------------------------------------------- list_my_posts

    internal static readonly string[] Statuses = { "DRAFT", "PENDING_REVIEW", "PUBLISHED", "REJECTED", "DISABLED", "ARCHIVED" };

    [McpServerTool(Name = "list_my_posts"), Description(
        "Мои посты в DMC с их статусами — что ещё не отправлено на модерацию, что уже в каталоге.")]
    public async Task<string> ListMyPosts(
        [Description("Только этот статус: DRAFT, PENDING_REVIEW, PUBLISHED, REJECTED, DISABLED, ARCHIVED; пусто — все")] string? status = null,
        [Description("Тип: POST_PROCESSOR (по умолчанию), MACHINE_SCHEMA, INTERPRETER, DIGITAL_MACHINE_KIT или ANY — все")] string? contentType = "POST_PROCESSOR")
    {
        string? want = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            want = status.Trim().ToUpperInvariant();
            if (!Statuses.Contains(want)) return $"ОШИБКА: статус «{want}» неизвестен. Есть: " + string.Join(", ", Statuses);
        }
        var (type, typeErr) = NormalizeType(contentType);
        if (typeErr != null) return typeErr;
        var (token, err) = await Token();
        if (token == null) return err!;
        IReadOnlyList<ProductInfo> mine;
        try { mine = await dmc.MyProducts(token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }

        var posts = mine.Where(p => (type == null || p.ContentType == type) && (want == null || p.PublicationStatus == want)).ToList();
        if (posts.Count == 0) return want == null ? "У вас пока нет постов в DMC." : $"Постов со статусом «{StatusWord(want)}» нет.";
        var sb = new StringBuilder();
        foreach (var p in posts) sb.AppendLine(Line(p, own: false));
        sb.AppendLine("Итого: " + string.Join(", ",
            posts.GroupBy(p => p.PublicationStatus).Select(g => $"{StatusWord(g.Key)}: {g.Count()}")));
        return sb.ToString();
    }

    // ------------------------------------------------------------------- delete_post

    [McpServerTool(Name = "delete_post"), Description(
        "Удалить свой черновик (или отклонённый пост) — например, залитый по ошибке. Опубликованное и " +
        "отправленное на модерацию не удаляет: снятие с публикации делается в кабинете осознанно.")]
    public async Task<string> DeletePost(
        [Description("id поста")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        if (p.PublicationStatus is not ("DRAFT" or "REJECTED"))
            return $"ОШИБКА: «{p.Name}» — {StatusWord(p.PublicationStatus)}; удалять можно только черновик или отклонённый. "
                 + "Снимите с публикации в кабинете.";
        try { await dmc.DeleteProduct(p.Id, token); }
        catch (DmcHttpException e) when (e.Status == 409) { return "ОШИБКА: DMC не удаляет — " + ErrorText(e.Body); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        return $"Черновик «{p.Name}» удалён.";
    }

    // ----------------------------------------------------------- ИИ по запросу

    [McpServerTool(Name = "generate_description"), Description(
        "Сгенерировать описание поста ИИ по полям карточки и сохранить его (save=false — только показать).")]
    public async Task<string> GenerateDescription(
        [Description("id поста")] string id,
        [Description("true (по умолчанию) — записать в карточку; false — только вернуть текст")] bool save = true)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        string text;
        try { text = await dmc.GenerateDescription(DmcJson.RequestFrom(p.Raw), token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: ИИ не ответил: " + e.Message; }
        if (!save) return text + "\n(не сохранено — save=false)";
        var (_, putErr) = await PutFields(p, new FieldSet(Description: text), token);
        return putErr ?? text + "\n(сохранено в карточку)";
    }

    [McpServerTool(Name = "regenerate_cover"), Description(
        "Новая обложка поста: archive — картинка из архива компонента (без ИИ), ai — рендер ИИ. Сохраняется в карточку.")]
    public async Task<string> RegenerateCover(
        [Description("id поста")] string id,
        [Description("archive (по умолчанию) или ai")] string source = "archive")
    {
        var src = source.Trim().ToLowerInvariant();
        if (src is not ("archive" or "ai")) return "ОШИБКА: источник обложки — archive или ai.";
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;

        string image;
        try
        {
            if (src == "archive")
            {
                var file = p.Raw.TryGetProperty("productFile", out var pf) && pf.ValueKind == JsonValueKind.String ? pf.GetString() : null;
                if (string.IsNullOrEmpty(file)) return "ОШИБКА: у поста нет архива — картинку брать неоткуда.";
                var found = await dmc.ArchivePreview(file, token);
                if (found == null) return "В архиве нет картинки — попробуйте source=ai.";
                image = found;
            }
            else image = await dmc.GenerateImage(DmcJson.RequestFrom(p.Raw), token);
        }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: не удалось получить обложку: " + e.Message; }

        var putErr = await PutRaw(p, "imageUrl", image, token);
        return putErr ?? $"Обложка «{p.Name}» обновлена (источник: {(src == "archive" ? "архив" : "ИИ")}).\nСсылка: {p.Url(dmc.Site)}";
    }

    [McpServerTool(Name = "generate_sample_code"), Description(
        "Сгенерировать ИИ пример NC-кода для поста и прикрепить его к карточке (Sample output code).")]
    public async Task<string> GenerateSampleCode(
        [Description("id поста")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        string path;
        try { path = await dmc.GenerateSampleCode(DmcJson.RequestFrom(p.Raw), token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: ИИ не ответил: " + e.Message; }
        var putErr = await PutRaw(p, "sampleOutputCodeFile", path, token);
        return putErr ?? $"Пример NC-кода сгенерирован и прикреплён к «{p.Name}».\nСсылка: {p.Url(dmc.Site)}";
    }

    [McpServerTool(Name = "generate_codes_list"), Description(
        "Сгенерировать ИИ список поддерживаемых G/M-кодов поста и прикрепить его к карточке (Supported codes).")]
    public async Task<string> GenerateCodesList(
        [Description("id поста")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        string path;
        try { path = await dmc.GenerateCodesList(DmcJson.RequestFrom(p.Raw), token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: ИИ не ответил: " + e.Message; }
        var putErr = await PutRaw(p, "supportedCodesFile", path, token);
        return putErr ?? $"Список кодов сгенерирован и прикреплён к «{p.Name}».\nСсылка: {p.Url(dmc.Site)}";
    }

    /** Карточка по id — или готовый текст ошибки (404 — и «нет такого», и «чужой черновик»). */
    private async Task<(ProductInfo? Product, string? Error)> Get(string id, string token)
    {
        try
        {
            var p = await dmc.GetProduct(id, token);
            return p == null ? (null, Explain(new DmcHttpException(404, ""))) : (p, null);
        }
        catch (DmcHttpException e) { return (null, Explain(e)); }
        catch (Exception e) { return (null, "ОШИБКА: DMC не ответил: " + e.Message); }
    }

    /** Полный PUT с одним изменённым полем — для обложки и файлов, которых нет в FieldSet. */
    private async Task<string?> PutRaw(ProductInfo p, string field, string value, string token)
    {
        var body = DmcJson.RequestFrom(p.Raw);
        body[field] = value;
        try { await dmc.UpdateProduct(p.Id, body, token); return null; }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
    }

    // ------------------------------------------------------------ импорт: общее

    /**
     * Отправляет файл в bulk-zip и ждёт конца импорта. Возвращает прогресс, либо готовый текст
     * ошибки — включая таймаут, когда сервер продолжает без нас.
     */
    internal async Task<(ImportProgress? Progress, string? Error)> Import(string path, bool ai, string? nameHint,
        string? descriptionHint, string token)
    {
        string importId = Guid.NewGuid().ToString("N");
        try { importId = await dmc.StartImport(path, importId, ai, nameHint, descriptionHint, token); }
        catch (DmcHttpException e) { return (null, Explain(e)); }
        catch (Exception e) { return (null, "ОШИБКА: не удалось отправить файл в DMC: " + e.Message); }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ImportProgress progress;
        while (true)
        {
            try { progress = await dmc.GetImportProgress(importId, token); }
            catch (DmcHttpException e) { return (null, Explain(e)); }
            catch (Exception e) { return (null, $"ОШИБКА: DMC не ответил про импорт {importId}: {e.Message}"); }
            if (progress.Finished) break;
            // «queued» — очередь за чужим импортом, не зависание; ждём так же.
            if (sw.Elapsed >= MaxWait)
                return (null, $"Импорт {importId} всё ещё идёт (сейчас: {progress.Status}, {progress.CurrentName}). "
                            + "Сервер продолжит сам — черновик появится в кабинете DMC в «My components». "
                            + "Повторно файл не отправляйте.");
            await Delay(PollEvery);
        }

        if (progress.Status == "error") return (null, "ОШИБКА импорта: " + (progress.StatusReason ?? "причина не названа"));
        if (progress.Status == "cancelled") return (null, "Импорт отменён на сервере.");
        if (progress.Components.Count == 0)
            return (null, "ОШИБКА: DMC не нашёл в файле компонента."
                        + (progress.Errors.Count > 0 ? "\n" + string.Join("\n", progress.Errors) : ""));
        return (progress, null);
    }

    /** Строки отчёта по каждому созданному черновику и по тому, что не принято. */
    internal async Task ReportComponents(StringBuilder sb, ImportProgress progress, string token)
    {
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
    }

    // ------------------------------------------------------------- поля: общее

    /** Поля карточки, которые инструменты меняют. Null — «не трогать». */
    internal sealed record FieldSet(string? Name = null, string? Description = null,
        string? ControllerManufacturer = null, string? ControllerSeries = null, string? ControllerModel = null,
        string? MachineManufacturer = null, string? MachineSeries = null, string? MachineModel = null,
        string? MachineType = null, int? NumberOfAxes = null)
    {
        public bool IsEmpty => Name == null && Description == null && ControllerManufacturer == null
            && ControllerSeries == null && ControllerModel == null && MachineManufacturer == null
            && MachineSeries == null && MachineModel == null && MachineType == null && NumberOfAxes == null;
    }

    /** Тип станка — к верхнему регистру и по списку бэкенда; оси — в пределах 1..12. Ошибка — готовым текстом. */
    internal static (FieldSet? Fields, string? Error) Normalize(FieldSet f)
    {
        if (f.MachineType != null)
        {
            var mt = f.MachineType.Trim().ToUpperInvariant();
            if (!MachineTypes.Contains(mt))
                return (null, $"ОШИБКА: тип станка «{mt}» неизвестен. Есть: " + string.Join(", ", MachineTypes));
            f = f with { MachineType = mt };
        }
        if (f.NumberOfAxes is < 1 or > 12) return (null, "ОШИБКА: число осей — от 1 до 12.");
        return (f, null);
    }

    /**
     * Полный PUT: тело — вся текущая карточка, поверх неё только переданные поля. Возвращает список
     * «поле: было → стало» (пустой — ничего не менялось, PUT не делался) или текст ошибки.
     */
    internal async Task<(List<string> Changes, string? Error)> PutFields(ProductInfo p, FieldSet f, string token)
    {
        var body = DmcJson.RequestFrom(p.Raw);
        var changes = new List<string>();
        void Set(string field, string? old, string? value)
        {
            if (value == null || value == old) return;
            body[field] = value;
            changes.Add($"{field}: {old ?? "—"} → {value}");
        }
        Set("name", p.Name, f.Name);
        Set("description", p.Description, f.Description);
        Set("controllerManufacturer", p.ControllerManufacturer, f.ControllerManufacturer);
        Set("controllerSeries", p.ControllerSeries, f.ControllerSeries);
        Set("controllerModel", p.ControllerModel, f.ControllerModel);
        Set("machineManufacturer", p.MachineManufacturer, f.MachineManufacturer);
        Set("machineSeries", p.MachineSeries, f.MachineSeries);
        Set("machineModel", p.MachineModel, f.MachineModel);
        Set("machineType", p.MachineType, f.MachineType);
        if (f.NumberOfAxes is int n && n != p.NumberOfAxes)
        {
            body["numberOfAxes"] = n;
            changes.Add($"numberOfAxes: {p.NumberOfAxes?.ToString() ?? "—"} → {n}");
        }
        if (changes.Count == 0) return (changes, null);

        try { await dmc.UpdateProduct(p.Id, body, token); }
        catch (DmcHttpException e) { return (changes, Explain(e)); }
        catch (Exception e) { return (changes, "ОШИБКА: DMC не ответил: " + e.Message); }
        return (changes, null);
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
