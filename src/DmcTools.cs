using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
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
        "Загрузить компонент (пост, схему, интерпретатор, кит) в Digital Machine Center черновиком: бэкенд " +
        "разбирает архив, ИИ дописывает описание, стойку, станок и обложку. Перед загрузкой ищет похожие по " +
        "имени и останавливается, если нашёл (force=true — залить всё равно). На модерацию НЕ отправляет — " +
        "проверьте черновик (update_post поправит поля) и вызовите submit_post.")]
    public async Task<string> PublishPost(
        [Description("Путь к файлу: .sppx, .dll, .stnci или .zip")] string file,
        [Description("Подсказка имени компонента, например «Fanuc 0i-MF для Haas VF-2»")] string? name = null,
        [Description("Подсказка для описания: особенности поста, для какого станка")] string? descriptionHint = null,
        [Description("true (по умолчанию) — ИИ дописывает описание, обложку и метаданные; false — только разбор архива")] bool ai = true,
        [Description("true — залить, даже если в DMC уже есть похожий по имени")] bool force = false,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ОШИБКА: файла {path} нет.";
        if (!PostExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"ОШИБКА: {Path.GetFileName(path)} — не пост. Нужен .sppx, .dll, .stnci или .zip.";

        var (token, err) = await Token();
        if (token == null) return err!;

        // Защита от дублей: в каталоге 165 постов делят 70 имён — ищем ДО загрузки, а не после.
        if (!force)
        {
            var hits = await Similar(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name!, token);
            if (hits.Count > 0) return SimilarText(hits);
        }

        var (result, importErr) = await Import(path, ai, name, descriptionHint, token, progress);
        if (result == null) return importErr!;

        var sb = new StringBuilder();
        await ReportComponents(sb, result, token);
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

    /** Что поедет в zip: папка-компонент и откуда она — файл поста или подпапка со схемой/китом. */
    private sealed record Planned(string Folder, string Source, string Path, bool IsDir, ManifestEntry? Entry);

    [McpServerTool(Name = "publish_folder"), Description(
        "Загрузить все компоненты из папки одним импортом — каждый становится отдельным черновиком: файлы " +
        "постов и подпапки со схемами или китами. Manifest (CSV: file,name,controllerManufacturer,…) даёт точные " +
        "имя и поля вместо догадок ИИ. dryRun=true — только показать план. Перед загрузкой ищет похожие по " +
        "имени и останавливается (force=true — залить всё равно). На модерацию не отправляет.")]
    public async Task<string> PublishFolder(
        [Description("Папка с компонентами: файлы постов (.sppx, .dll, .stnci, .zip) и подпапки — схема (xml + osd) или кит папкой")] string dir,
        [Description("Путь к CSV-манифесту: колонки file, name, description, controllerManufacturer, " +
                     "controllerSeries, controllerModel, machineManufacturer, machineSeries, machineModel, " +
                     "machineType, numberOfAxes; обязательна только file")] string? manifest = null,
        [Description("true (по умолчанию) — ИИ дописывает описание, обложку и метаданные; false — только разбор архива")] bool ai = true,
        [Description("Подсказка ИИ по именам — как «Naming legend» в кабинете, например «M3X = 3-axis mill»")] string? nameHint = null,
        [Description("Подсказка ИИ для описаний")] string? descriptionHint = null,
        [Description("true — только показать план (компоненты, manifest, похожие в DMC), ничего не отправлять")] bool dryRun = false,
        [Description("true — залить, даже если в DMC уже есть похожие по имени")] bool force = false,
        IProgress<ProgressNotificationValue>? progress = null)
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

        // План: папка на компонент — так bulk-zip делит архив на черновики и берёт имя из папки.
        var plan = new List<Planned>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, isDir) in files.Select(f => (f, false)).Concat(dirs.Select(d => (d, true))))
        {
            var source = Path.GetFileName(path);
            var entry = man?.For(source);
            var folder = SafeFolderName(string.IsNullOrWhiteSpace(entry?.Name)
                ? (isDir ? source : Path.GetFileNameWithoutExtension(path))
                : entry!.Name!);
            var unique = folder;
            for (int n = 2; taken.Contains(unique); n++) unique = $"{folder} ({n})";
            taken.Add(unique);
            plan.Add(new Planned(unique, source, path, isDir, entry));
        }

        var (token, err) = await Token();
        if (token == null) return err!;

        // Похожие — по каждому запланированному имени; в сухом прогоне это справка, иначе — стоп.
        var similar = new List<(ProductInfo P, bool Own)>();
        if (dryRun || !force)
            foreach (var p in plan)
                foreach (var hit in await Similar(p.Folder, token))
                    if (similar.All(s => s.P.Id != hit.P.Id)) similar.Add(hit);

        if (dryRun)
        {
            var sb = new StringBuilder("Сухой прогон — ничего не отправлено. План:\n");
            foreach (var p in plan)
                sb.AppendLine($"{p.Folder} ← {p.Source}" + (p.IsDir ? " (папка)" : "")
                              + (p.Entry == null ? "" : " (из manifest: " + ManifestSummary(p.Entry) + ")"));
            foreach (var note in notes) sb.AppendLine(note);
            if (similar.Count > 0)
            {
                sb.AppendLine("Похожие уже есть в DMC:");
                foreach (var (p, own) in similar) sb.AppendLine(Line(p, own));
            }
            return sb.ToString();
        }
        if (!force && similar.Count > 0) return SimilarText(similar);

        var folderOf = plan.ToDictionary(p => p.Folder, p => p.Source, StringComparer.OrdinalIgnoreCase);
        var zipPath = Path.Combine(Path.GetTempPath(), "dmc-folder-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var p in plan)
                {
                    if (p.IsDir)
                        foreach (var f in Directory.GetFiles(p.Path, "*", SearchOption.AllDirectories))
                            zip.CreateEntryFromFile(f, p.Folder + "/" + Path.GetRelativePath(p.Path, f).Replace('\\', '/'));
                    else zip.CreateEntryFromFile(p.Path, p.Folder + "/" + p.Source);
                }
            if (new FileInfo(zipPath).Length > MaxUploadBytes)
                return "ОШИБКА: архив получился больше 1 ГБ — разбейте папку на части.";

            var (result, importErr) = await Import(zipPath, ai, nameHint, descriptionHint, token, progress);
            if (result == null) return importErr!;

            var sb = new StringBuilder();
            foreach (var note in notes) sb.AppendLine(note);
            await ReportComponents(sb, result, token);
            if (man != null) await ApplyManifest(sb, result, man, folderOf, token);
            sb.AppendLine($"Итого {result.Components.Count} черновик(ов). Проверьте (audit_drafts) и отправьте готовые — "
                          + "submit_post по одному или submit_drafts(\"ALL\").");
            return sb.ToString();
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* временный файл */ }
        }
    }

    /** Коротко, что manifest задаёт для строки: name=…, controllerManufacturer=…. */
    private static string ManifestSummary(ManifestEntry e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Name)) parts.Add("name=" + e.Name);
        var f = e.Fields;
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) parts.Add($"{k}={v}"); }
        Add("description", f.Description);
        Add("controllerManufacturer", f.ControllerManufacturer);
        Add("controllerSeries", f.ControllerSeries);
        Add("controllerModel", f.ControllerModel);
        Add("machineManufacturer", f.MachineManufacturer);
        Add("machineSeries", f.MachineSeries);
        Add("machineModel", f.MachineModel);
        Add("machineType", f.MachineType);
        if (f.NumberOfAxes is int n) parts.Add("numberOfAxes=" + n);
        return parts.Count == 0 ? "пусто" : string.Join(", ", parts);
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

    // ------------------------------------------------------------------ check_import

    [McpServerTool(Name = "check_import"), Description(
        "Результат импорта по importId — когда publish_post или publish_folder не дождались конца и отдали id. " +
        "Ничего не отправляет заново.")]
    public async Task<string> CheckImport(
        [Description("importId из ответа publish_post / publish_folder")] string importId,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (result, waitErr) = await WaitImport(importId.Trim(), token, progress);
        if (result == null) return waitErr!;
        var sb = new StringBuilder();
        await ReportComponents(sb, result, token);
        sb.AppendLine("Проверьте черновики (audit_drafts) и отправьте готовые — submit_drafts(\"ALL\").");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ audit_drafts

    [McpServerTool(Name = "audit_drafts"), Description(
        "Проверить свои черновики по правилам модерации: что готово к отправке, чему чего не хватает " +
        "(имя, производитель станка, тип станка, архив), у кого нет обложки или описания.")]
    public async Task<string> AuditDrafts()
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        IReadOnlyList<ProductInfo> mine;
        try { mine = await dmc.MyProducts(token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }

        var drafts = mine.Where(p => p.PublicationStatus == "DRAFT").ToList();
        if (drafts.Count == 0) return "Черновиков нет.";
        var sb = new StringBuilder();
        int ready = 0;
        foreach (var p in drafts)
        {
            var missing = Missing(p);
            var soft = new List<string>();
            if (!p.HasCover) soft.Add("без обложки");
            if (string.IsNullOrWhiteSpace(p.Description)) soft.Add("без описания");
            if (missing.Count == 0)
            {
                ready++;
                sb.AppendLine($"{Title(p)} — готов к отправке" + (soft.Count > 0 ? $" ({string.Join(", ", soft)})" : ""));
            }
            else
                sb.AppendLine($"{Title(p)} — не хватает: {string.Join(", ", missing)}" + (soft.Count > 0 ? $"; {string.Join(", ", soft)}" : ""));
        }
        sb.AppendLine($"Готовы: {ready}, не готовы: {drafts.Count - ready}. Отправить готовые — submit_drafts(\"ALL\").");
        return sb.ToString();
    }

    // ----------------------------------------------------------------- submit_drafts

    [McpServerTool(Name = "submit_drafts"), Description(
        "Отправить на модерацию несколько черновиков: id через запятую или ALL — все свои готовые. " +
        "Неготовые перечисляет с тем, чего не хватает.")]
    public async Task<string> SubmitDrafts(
        [Description("id через запятую, или ALL")] string ids)
    {
        var (token, err) = await Token();
        if (token == null) return err!;

        List<ProductInfo> targets;
        if (ids.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<ProductInfo> mine;
            try { mine = await dmc.MyProducts(token); }
            catch (DmcHttpException e) { return Explain(e); }
            catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
            targets = mine.Where(p => p.PublicationStatus == "DRAFT").ToList();
            if (targets.Count == 0) return "У вас нет черновиков.";
        }
        else
        {
            var list = ids.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().ToList();
            if (list.Count == 0) return "ОШИБКА: не передано ни одного id.";
            targets = new List<ProductInfo>();
            foreach (var id in list)
            {
                var (p, getErr) = await Get(id, token);
                if (p == null) return getErr!;
                targets.Add(p);
            }
        }

        var sb = new StringBuilder();
        foreach (var p in targets)
        {
            if (p.PublicationStatus is "PENDING_REVIEW" or "PUBLISHED")
            {
                sb.AppendLine($"{Title(p)} — уже {StatusWord(p.PublicationStatus)}");
                continue;
            }
            var missing = Missing(p);
            if (missing.Count > 0) { sb.AppendLine($"{Title(p)} — не хватает: {string.Join(", ", missing)}"); continue; }
            try { await dmc.SetStatus(p.Id, "PENDING_REVIEW", token); sb.AppendLine($"{Title(p)} — отправлен на модерацию"); }
            catch (DmcHttpException e) when (e.Status == 400) { sb.AppendLine($"{Title(p)} — DMC не принял: {ErrorText(e.Body)}"); }
            catch (DmcHttpException e) { sb.AppendLine($"{Title(p)} — {Explain(e)}"); }
            catch (Exception e) { sb.AppendLine($"{Title(p)} — DMC не ответил: {e.Message}"); }
        }
        return sb.ToString();
    }

    // ----------------------------------------------------------------- describe_post

    [McpServerTool(Name = "describe_post"), Description(
        "Полная карточка компонента: описание целиком, стойка и станок, обложка, файлы, цена и триал, связи — " +
        "чтобы оценить, что дописал ИИ. Принимает id или slug.")]
    public async Task<string> DescribePost(
        [Description("id или slug")] string idOrSlug)
    {
        var (token, _) = await Token(); // опубликованное видно и без входа
        ProductInfo? p;
        try { p = await dmc.GetProduct(idOrSlug, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null) return $"DMC не нашёл «{idOrSlug}» — нет такого, или это чужой черновик.";

        var sb = new StringBuilder();
        sb.AppendLine($"{p.Name} [{p.ContentType}] — {StatusWord(p.PublicationStatus)}");
        sb.AppendLine($"Ссылка: {p.Url(dmc.Site)}   id: {p.Id}");
        sb.AppendLine($"Стойка: {p.Controller}");
        sb.AppendLine($"Станок: {p.Machine}" + (p.MachineType != null ? $", тип {p.MachineType}" : "")
                      + (p.NumberOfAxes is int n ? $", осей {n}" : ""));
        sb.AppendLine("Описание: " + (string.IsNullOrWhiteSpace(p.Description) ? "нет" : p.Description));
        sb.AppendLine("Обложка: " + (RawStr(p, "imageUrl") ?? "нет"));
        sb.AppendLine("Архив: " + (RawStr(p, "productFile") ?? "нет"));
        sb.AppendLine("Пример кода: " + (RawStr(p, "sampleOutputCodeFile") ?? "нет")
                      + "; список кодов: " + (RawStr(p, "supportedCodesFile") ?? "нет"));
        var price = RawNum(p, "priceEur");
        var trial = RawNum(p, "trialDays");
        sb.AppendLine("Цена: " + (price == null ? "не задана" : price == -1 ? "в составе maintenance" : price == 0 ? "бесплатно" : $"{price} €")
                      + (trial != null ? $", триал {trial} дн." : ""));
        IReadOnlyList<LinkInfo> links;
        try { links = await dmc.GetLinks(p.Id, token); }
        catch (Exception) { links = Array.Empty<LinkInfo>(); }
        var named = links.Where(l => l.Product != null).Select(l => $"{l.Product!.Name} ({l.LinkType})").ToList();
        sb.AppendLine("Связи: " + (named.Count == 0 ? "нет" : string.Join(", ", named)));
        return sb.ToString();
    }

    // --------------------------------------------------- готовность и похожие

    /** Чего не хватает для модерации — те же четыре правила, что у бэкенда в updateStatus. */
    internal static List<string> Missing(ProductInfo p)
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name)) m.Add("имя");
        if (string.IsNullOrWhiteSpace(p.MachineManufacturer)) m.Add("производитель станка");
        if (p.MachineType == null) m.Add("тип станка");
        if (string.IsNullOrWhiteSpace(RawStr(p, "productFile"))) m.Add("архив");
        return m;
    }

    private static string Title(ProductInfo p) =>
        string.IsNullOrWhiteSpace(p.Name) ? $"(без имени, id {p.Id})" : $"{p.Name} (id {p.Id})";

    internal static string? RawStr(ProductInfo p, string key) =>
        p.Raw.ValueKind == JsonValueKind.Object && p.Raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static decimal? RawNum(ProductInfo p, string key) =>
        p.Raw.ValueKind == JsonValueKind.Object && p.Raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal() : null;

    /**
     * Похожие по имени — опубликованные из каталога и свои любого статуса. Похожим считаем, когда имя
     * содержит запрос или запрос содержит имя: «Fanuc 0i» найдёт «Fanuc 0i for Haas VF-2». Сбой любого
     * из источников защиту не валит — заливка важнее справки.
     */
    private async Task<List<(ProductInfo P, bool Own)>> Similar(string query, string token)
    {
        var q = query.Trim();
        var hits = new List<(ProductInfo P, bool Own)>();
        if (q.Length == 0) return hits;
        static bool Like(ProductInfo p, string q) =>
            p.Name.Length > 0 && (p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || q.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
        try { foreach (var p in await dmc.Search(null, q, null, null, token)) if (Like(p, q)) hits.Add((p, false)); }
        catch (Exception) { /* каталог не ответил — проверим хотя бы своё */ }
        try
        {
            foreach (var p in await dmc.MyProducts(token))
                if (Like(p, q) && hits.All(h => h.P.Id != p.Id)) hits.Add((p, true));
        }
        catch (Exception) { /* свои не прочитались */ }
        return hits;
    }

    private string SimilarText(List<(ProductInfo P, bool Own)> hits)
    {
        var sb = new StringBuilder("Похожие уже есть:\n");
        foreach (var (p, own) in hits) sb.AppendLine(Line(p, own));
        sb.AppendLine("Повторите с force=true, чтобы всё же залить, или обновите существующий: update_post / replace_post_file.");
        return sb.ToString();
    }

    // ------------------------------------------------------------ импорт: общее

    /**
     * Отправляет файл в bulk-zip и ждёт конца импорта. Возвращает прогресс, либо готовый текст
     * ошибки — включая таймаут, когда сервер продолжает без нас.
     */
    internal async Task<(ImportProgress? Progress, string? Error)> Import(string path, bool ai, string? nameHint,
        string? descriptionHint, string token, IProgress<ProgressNotificationValue>? progress = null)
    {
        string importId = Guid.NewGuid().ToString("N");
        try { importId = await dmc.StartImport(path, importId, ai, nameHint, descriptionHint, token); }
        catch (DmcHttpException e) { return (null, Explain(e)); }
        catch (Exception e) { return (null, "ОШИБКА: не удалось отправить файл в DMC: " + e.Message); }
        return await WaitImport(importId, token, progress);
    }

    /**
     * Ждёт конца импорта по importId: прогресс — или готовый текст (таймаут, ошибка сервера, чужой
     * id). Прогресс уходит и в редактор, если тот прислал progressToken: на минуты молчания иначе
     * непонятно, жив ли инструмент.
     */
    internal async Task<(ImportProgress? Progress, string? Error)> WaitImport(string importId, string token,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ImportProgress state;
        while (true)
        {
            try { state = await dmc.GetImportProgress(importId, token); }
            catch (DmcHttpException e) when (e.Status == 404) { return (null, $"ОШИБКА: импорт {importId} не найден — старый или чужой."); }
            catch (DmcHttpException e) { return (null, Explain(e)); }
            catch (Exception e) { return (null, $"ОШИБКА: DMC не ответил про импорт {importId}: {e.Message}"); }
            progress?.Report(new ProgressNotificationValue
            {
                Progress = state.Done,
                Total = state.Total > 0 ? state.Total : null,
                Message = state.Finished ? "готово" : $"{state.Status}: {state.CurrentName}",
            });
            if (state.Finished) break;
            // «queued» — очередь за чужим импортом, не зависание; ждём так же.
            if (sw.Elapsed >= MaxWait)
                return (null, $"Импорт {importId} всё ещё идёт (сейчас: {state.Status}, {state.CurrentName}). "
                            + "Сервер продолжит сам — черновик появится в кабинете DMC в «My components», "
                            + $"а результат покажет check_import(\"{importId}\"). Повторно файл не отправляйте.");
            await Delay(PollEvery);
        }

        if (state.Status == "error") return (null, "ОШИБКА импорта: " + (state.StatusReason ?? "причина не названа"));
        if (state.Status == "cancelled") return (null, "Импорт отменён на сервере.");
        if (state.Components.Count == 0)
            return (null, "ОШИБКА: DMC не нашёл в файле компонента."
                        + (state.Errors.Count > 0 ? "\n" + string.Join("\n", state.Errors) : ""));
        return (state, null);
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
