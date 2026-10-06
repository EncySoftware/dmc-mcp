using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DmcMcp;

/// <summary>
/// Tools for publishing a post to DMC from the editor. Publishing goes through the backend's bulk-zip: it
/// unpacks the archive itself and has AI fill in the fields, so the agent does not fill in the card by hand.
/// Every answer is text for a human; an error starts with "ERROR:", and no exception escapes.
/// </summary>
[McpServerToolType]
public class DmcTools(IDmcClient dmc, DmcTokenProvider tokens)
{
    /** Replaced in tests: a real sleep while polling is pointless there. */
    internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;
    internal TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    /** The backend's MachineType values (model/MachineType.java, 2026-09-16). */
    internal static readonly string[] MachineTypes =
    {
        "MILLING", "TURNING", "MILL_TURN", "WIRE_EDM", "LASER", "PLASMA", "WATERJET", "GRINDING",
        "ROBOT", "EDM", "ROUTER", "SWISS", "GAS_PLASMA_LASER", "ADDITIVE", "OTHER",
    };

    /** What the backend's bulk-zip accepts as a single post (BulkZipImportService, 2026-09-16). */
    internal static readonly string[] PostExtensions = { ".sppx", ".dll", ".stnci", ".zip" };

    internal const string NoLogin =
        "ERROR: not signed in to DMC on this machine — run `" + Brand.Cli + " login` once in a terminal.";

    // ------------------------------------------------------------- check_post_status

    [McpServerTool(Name = "check_post_status"), Description(
        "What DMC knows about a post: status (draft / pending moderation / published), card link, " +
        "control and machine. Takes an id or a slug.")]
    public async Task<string> CheckPostStatus(
        [Description("Post id or slug")] string idOrSlug)
    {
        var (token, _) = await Token(); // works without sign-in too: published posts are visible to everyone
        ProductInfo? p;
        try { p = await dmc.GetProduct(idOrSlug, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        if (p == null)
            return $"DMC did not find \"{idOrSlug}\" — it does not exist, or it is someone else's draft"
                   + (token == null ? $" (not signed in: run `{Brand.Cli} login` to see your own drafts)." : ".");
        return Describe(p);
    }

    // ------------------------------------------------------------------ publish_post

    [McpServerTool(Name = "publish_post"), Description(
        "Upload a component (post, schema, interpreter, kit) to Digital Machine Center as a draft: the backend " +
        "unpacks the archive, AI fills in the description, control, machine and cover. Before uploading it looks " +
        "for similar names and stops if it finds any (force=true uploads anyway). Does NOT submit for moderation — " +
        "check the draft (update_post fixes fields) and call submit_post.")]
    public async Task<string> PublishPost(
        [Description("File path: .sppx, .dll, .stnci or .zip")] string file,
        [Description("Component name hint, e.g. \"Fanuc 0i-MF for Haas VF-2\"")] string? name = null,
        [Description("Description hint: the post's specifics, which machine it is for")] string? descriptionHint = null,
        [Description("true (default) — AI fills in the description, cover and metadata; false — archive parsing only")] bool ai = true,
        [Description("true — upload even if DMC already has one with a similar name")] bool force = false,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ERROR: file {path} does not exist.";
        if (!PostExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"ERROR: {Path.GetFileName(path)} is not a post. Expected .sppx, .dll, .stnci or .zip.";

        var (token, err) = await Token();
        if (token == null) return err!;

        // Duplicate guard: in the catalogue 165 posts share 70 names — search BEFORE the upload, not after.
        if (!force)
        {
            var hits = await Similar(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name!, token);
            if (hits.Count > 0) return SimilarText(hits);
        }

        var (result, importErr) = await Import(path, ai, name, descriptionHint, token, progress);
        if (result == null) return importErr!;

        var sb = new StringBuilder();
        await ReportComponents(sb, result, token);
        sb.AppendLine("Check the control, machine and description (update_post fixes them), then submit_post for moderation.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------- update_post

    [McpServerTool(Name = "update_post"), Description(
        "Fix card fields (of a post, schema or kit) that AI guessed wrong: name, description, control, machine, " +
        "machine type, number of axes, X/Y/Z axis travels. Leaves files, price and status alone. Pass only what you change.")]
    public async Task<string> UpdatePost(
        [Description("Post id")] string id,
        [Description("Component name")] string? name = null,
        [Description("Description")] string? description = null,
        [Description("Control maker, e.g. Fanuc")] string? controllerManufacturer = null,
        [Description("Control series, e.g. 0i")] string? controllerSeries = null,
        [Description("Control model, e.g. MF")] string? controllerModel = null,
        [Description("Machine maker, e.g. Haas")] string? machineManufacturer = null,
        [Description("Machine series, e.g. VF")] string? machineSeries = null,
        [Description("Machine model, e.g. VF-2")] string? machineModel = null,
        [Description("Machine type: MILLING, TURNING, MILL_TURN, WIRE_EDM, LASER, PLASMA, WATERJET, GRINDING, " +
                     "ROBOT, EDM, ROUTER, SWISS, GAS_PLASMA_LASER, ADDITIVE, OTHER")] string? machineType = null,
        [Description("Number of axes, 1 to 12")] int? numberOfAxes = null,
        [Description("X-axis travel (work area), mm, e.g. 508")] double? travelXMm = null,
        [Description("Y-axis travel, mm, e.g. 406.4")] double? travelYMm = null,
        [Description("Z-axis travel, mm, e.g. 508")] double? travelZMm = null)
    {
        // Checks before going to DMC: the author learns of a typo in the machine type at once, not after the PUT.
        var fields = new FieldSet(name, description, controllerManufacturer, controllerSeries, controllerModel,
            machineManufacturer, machineSeries, machineModel, machineType, numberOfAxes,
            travelXMm, travelYMm, travelZMm);
        var (normalized, fieldErr) = Normalize(fields);
        if (normalized == null) return fieldErr!;

        var (token, err) = await Token();
        if (token == null) return err!;

        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));

        var (changes, putErr) = await PutFields(p, normalized, token);
        if (putErr != null) return putErr;
        if (changes.Count == 0) return "Nothing to change — all the values passed are already set.";
        return "Changed:\n" + string.Join("\n", changes) + "\nLink: " + p.Url(dmc.Site);
    }

    // ------------------------------------------------------------------- submit_post

    [McpServerTool(Name = "submit_post"), Description(
        "Submit a draft for moderation. DMC requires a name, machine maker, machine type and archive — " +
        "if something is missing, it says so; fill it in with update_post and try again.")]
    public async Task<string> SubmitPost(
        [Description("Post id")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;

        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));
        if (p.PublicationStatus is "PENDING_REVIEW" or "PUBLISHED")
            return $"{p.Name} is already {StatusWord(p.PublicationStatus)} — nothing changed.\nLink: {p.Url(dmc.Site)}";

        try { p = await dmc.SetStatus(id, "PENDING_REVIEW", token); }
        catch (DmcHttpException e) when (e.Status == 400)
        {
            return "ERROR: DMC did not accept it for moderation — " + ErrorText(e.Body)
                 + ". Fill in what is missing with update_post and try again.";
        }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }

        return $"{p.Name} submitted for moderation — it will appear in the catalogue once approved.\nLink: {p.Url(dmc.Site)}";
    }

    // -------------------------------------------------------------------- find_posts

    [McpServerTool(Name = "find_posts"), Description(
        "Find posts before uploading, so as not to create duplicates: those published in the catalogue and your " +
        "own (drafts too). At least one criterion: text (name, machine model, a word from the description), " +
        "control maker, machine maker.")]
    public async Task<string> FindPosts(
        [Description("Text: post name, machine model, a word from the description")] string? query = null,
        [Description("Control maker, e.g. Fanuc")] string? controllerManufacturer = null,
        [Description("Machine maker, e.g. Haas")] string? machineManufacturer = null,
        [Description("Type: POST_PROCESSOR (default), MACHINE_SCHEMA, INTERPRETER, DIGITAL_MACHINE_KIT or ANY for all")] string? contentType = "POST_PROCESSOR")
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(controllerManufacturer)
            && string.IsNullOrWhiteSpace(machineManufacturer))
            return "ERROR: give at least something — text, a control or a machine maker.";
        var (type, typeErr) = NormalizeType(contentType);
        if (typeErr != null) return typeErr;

        var (token, _) = await Token(); // the catalogue is visible without sign-in; your own drafts only with it
        IReadOnlyList<ProductInfo> published;
        try { published = await dmc.Search(type, query, controllerManufacturer, machineManufacturer, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }

        var mine = new List<ProductInfo>();
        if (token != null)
        {
            try
            {
                mine = (await dmc.MyProducts(token))
                    .Where(p => (type == null || p.ContentType == type) && Matches(p, query, controllerManufacturer, machineManufacturer))
                    .ToList();
            }
            catch (Exception) { /* your own could not be read — show at least the catalogue */ }
        }

        var mineIds = mine.Select(p => p.Id).ToHashSet();
        var sb = new StringBuilder();
        foreach (var p in mine) sb.AppendLine(Line(p, own: true));
        foreach (var p in published) if (!mineIds.Contains(p.Id)) sb.AppendLine(Line(p, own: false));
        if (sb.Length == 0) sb.AppendLine("DMC found nothing — you can publish.");
        if (token == null) sb.AppendLine($"Your drafts were not checked — not signed in (`{Brand.Cli} login`).");
        return sb.ToString();
    }

    private string Line(ProductInfo p, bool own) =>
        $"{(own ? "[yours] " : "")}{(p.ContentType == "POST_PROCESSOR" ? "" : $"[{p.ContentType}] ")}{p.Name} — "
        + $"{StatusWord(p.PublicationStatus)} — control {p.Controller} — machine {p.Machine} — {p.Url(dmc.Site)} — id {p.Id}";

    /** The backend's component types (model/ContentType.java). */
    internal static readonly string[] ContentTypes = { "POST_PROCESSOR", "MACHINE_SCHEMA", "INTERPRETER", "DIGITAL_MACHINE_KIT" };

    /** Type from the parameter: null for any (ANY, ALL or empty), else one of ContentTypes. Errors come as ready text. */
    private static (string? Type, string? Error) NormalizeType(string? contentType)
    {
        var t = (contentType ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0 || t == "ANY" || t == "ALL") return (null, null);
        if (!ContentTypes.Contains(t))
            return (null, $"ERROR: unknown type \"{t}\". Valid: " + string.Join(", ", ContentTypes) + ", ANY");
        return (t, null);
    }

    /** Your own all come back — filter them here, on the same fields the catalogue searches by query. */
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
        "Replace the archive of an existing post with a new version: the file goes to storage, the card is " +
        "updated, the old archive is deleted. A published post's new archive goes live in the catalogue at once, " +
        "without re-moderation.")]
    public async Task<string> ReplacePostFile(
        [Description("Post id")] string id,
        [Description("Path to the new file: .sppx, .dll, .stnci or .zip")] string file)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ERROR: file {path} does not exist.";
        if (!PostExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"ERROR: {Path.GetFileName(path)} is not a post. Expected .sppx, .dll, .stnci or .zip.";

        var (token, err) = await Token();
        if (token == null) return err!;

        // Read the card BEFORE uploading: someone else's or a nonexistent id must not cost a file in tmp/.
        ProductInfo? p;
        try { p = await dmc.GetProduct(id, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        if (p == null) return Explain(new DmcHttpException(404, ""));

        string staged;
        try { staged = await dmc.UploadFile(path, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: could not upload the file to DMC: " + e.Message; }

        // A full PUT with the tmp/… path: the backend moves the file to the product, deletes the old one, re-reads
        // the axis travels and updates the licence container for those who already hold the post.
        var body = DmcJson.RequestFrom(p.Raw);
        body["productFile"] = staged;
        try { await dmc.UpdateProduct(p.Id, body, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }

        var sb = new StringBuilder();
        sb.AppendLine($"Archive of \"{p.Name}\" replaced with {Path.GetFileName(path)}; the old one deleted, licence holders' copies updated.");
        if (p.PublicationStatus == "PUBLISHED")
            sb.AppendLine("The post is published — the new archive goes live in the catalogue at once, without re-moderation.");
        sb.AppendLine("Link: " + p.Url(dmc.Site));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- publish_folder

    /** The backend's limit for one bulk-zip upload (spring.servlet.multipart, 1 GB). */
    internal const long MaxUploadBytes = 1024L * 1024 * 1024;

    /** What goes into the zip: a component folder and its source — a post file or a subfolder with a schema/kit. */
    private sealed record Planned(string Folder, string Source, string Path, bool IsDir, ManifestEntry? Entry);

    [McpServerTool(Name = "publish_folder"), Description(
        "Upload every component in a folder in one import — each becomes its own draft: post files " +
        "and subfolders with schemas or kits. A manifest (CSV: file,name,controllerManufacturer,…) gives exact " +
        "names and fields instead of AI guesses. dryRun=true only shows the plan. Before uploading it looks for " +
        "similar names and stops (force=true uploads anyway). Does not submit for moderation.")]
    public async Task<string> PublishFolder(
        [Description("Folder with components: post files (.sppx, .dll, .stnci, .zip) and subfolders — a schema (xml + osd) or a kit")] string dir,
        [Description("Path to the CSV manifest: columns file, name, description, controllerManufacturer, " +
                     "controllerSeries, controllerModel, machineManufacturer, machineSeries, machineModel, " +
                     "machineType, numberOfAxes, travelXMm, travelYMm, travelZMm; only file is required")] string? manifest = null,
        [Description("true (default) — AI fills in the description, cover and metadata; false — archive parsing only")] bool ai = true,
        [Description("AI hint for names — like \"Naming legend\" in the DMC account, e.g. \"M3X = 3-axis mill\"")] string? nameHint = null,
        [Description("AI hint for descriptions")] string? descriptionHint = null,
        [Description("true — only show the plan (components, manifest, similar ones in DMC) and send nothing")] bool dryRun = false,
        [Description("true — upload even if DMC already has ones with similar names")] bool force = false,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var dirPath = Path.GetFullPath(dir);
        if (!Directory.Exists(dirPath)) return $"ERROR: folder {dirPath} does not exist.";
        var files = Directory.GetFiles(dirPath)
            .Where(f => PostExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A subfolder is a component too: a schema (xml + osd) or a kit goes as a folder; bulk-zip unpacks it itself.
        var dirs = Directory.GetDirectories(dirPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0 && dirs.Count == 0)
            return $"ERROR: {dirPath} has no post files (.sppx, .dll, .stnci, .zip) and no subfolders.";

        Manifest? man = null;
        var notes = new List<string>();
        if (manifest != null)
        {
            var mp = Path.GetFullPath(manifest);
            if (!File.Exists(mp)) return $"ERROR: manifest {mp} not found.";
            try { man = Manifest.Parse(mp); }
            catch (InvalidDataException e) { return "ERROR: manifest — " + e.Message; }
            var present = files.Concat(dirs).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in man.Files)
                if (!present.Contains(f)) notes.Add($"The manifest lists {f}, but it is not in the folder — row skipped.");
        }

        // Plan: one folder per component — that is how bulk-zip splits the archive into drafts and names each draft after its folder.
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

        // Similar ones for every planned name: in a dry run that is information, otherwise a stop.
        var similar = new List<(ProductInfo P, bool Own)>();
        if (dryRun || !force)
            foreach (var p in plan)
                foreach (var hit in await Similar(p.Folder, token))
                    if (similar.All(s => s.P.Id != hit.P.Id)) similar.Add(hit);

        if (dryRun)
        {
            var sb = new StringBuilder("Dry run — nothing sent. Plan:\n");
            foreach (var p in plan)
                sb.AppendLine($"{p.Folder} ← {p.Source}" + (p.IsDir ? " (folder)" : "")
                              + (p.Entry == null ? "" : " (from manifest: " + ManifestSummary(p.Entry) + ")"));
            foreach (var note in notes) sb.AppendLine(note);
            if (similar.Count > 0)
            {
                sb.AppendLine("Similar ones already in DMC:");
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
                return "ERROR: the archive came out larger than 1 GB — split the folder into parts.";

            var (result, importErr) = await Import(zipPath, ai, nameHint, descriptionHint, token, progress);
            if (result == null) return importErr!;

            var sb = new StringBuilder();
            foreach (var note in notes) sb.AppendLine(note);
            await ReportComponents(sb, result, token);
            if (man != null) await ApplyManifest(sb, result, man, folderOf, token);
            sb.AppendLine($"Total: {result.Components.Count} draft(s). Check them (audit_drafts) and submit the ready ones — "
                          + "submit_post one by one or submit_drafts(\"ALL\").");
            return sb.ToString();
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* a temporary file */ }
        }
    }

    /** In short, what the manifest sets for a row: name=…, controllerManufacturer=…. */
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
        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }

    /** The folder name in the zip, from the component name: characters forbidden in paths become a hyphen. */
    internal static string SafeFolderName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => bad.Contains(ch) || ch == '/' || ch == '\\' ? '-' : ch).ToArray())
            .Trim().TrimEnd('.');
        return s.Length == 0 ? "post" : s;
    }

    /** The name bulk-zip gives the folder back under: it only replaces "_" between digits with "/". */
    private static string MatchKey(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "(?<=\\d)_(?=\\d)", "/").Trim();

    /** Manifest fields go onto every created draft by PUT; each failure becomes a line in the report. */
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
            if (normalized == null) { sb.AppendLine($"{c.Name}: manifest not applied — {fieldErr}"); continue; }
            ProductInfo? p = null;
            try { p = await dmc.GetProduct(c.ProductId, token); }
            catch (Exception) { /* reported as a line below */ }
            if (p == null) { sb.AppendLine($"{c.Name}: manifest not applied — the card could not be read."); continue; }
            var (changes, putErr) = await PutFields(p, normalized, token);
            if (putErr != null) { sb.AppendLine($"{c.Name}: manifest not applied — {putErr}"); continue; }
            if (changes.Count > 0) sb.AppendLine($"{c.Name}: from manifest — " + string.Join("; ", changes));
        }
    }

    // ---------------------------------------------------------------- search_schemas

    [McpServerTool(Name = "search_schemas"), Description(
        "Find machine schemas in the catalogue — to link a post to them (link_post_to_machines). " +
        "At least one criterion: text (machine model or name) or machine maker.")]
    public async Task<string> SearchSchemas(
        [Description("Text: machine model or name, e.g. VF-2")] string? query = null,
        [Description("Machine maker, e.g. Haas")] string? machineManufacturer = null)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(machineManufacturer))
            return "ERROR: give a text or a machine maker.";
        var (token, _) = await Token();
        IReadOnlyList<ProductInfo> found;
        try { found = await dmc.Search("MACHINE_SCHEMA", query, null, machineManufacturer, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        if (found.Count == 0) return "DMC found no schemas for this query.";
        var sb = new StringBuilder();
        foreach (var p in found)
            sb.AppendLine($"{p.Name} — machine {p.Machine}"
                          + (p.MachineType != null ? $", type {p.MachineType}" : "")
                          + (p.NumberOfAxes is int n ? $", {n} axes" : "")
                          + $" — {p.Url(dmc.Site)} — id {p.Id}");
        return sb.ToString();
    }

    // -------------------------------------------------------- link_post_to_machines

    [McpServerTool(Name = "link_post_to_machines"), Description(
        "Link a post to the machine schemas it is made for (a MADE_FOR link): the cards will show " +
        "\"made for\" and \"recommended posts\". Schema ids come from search_schemas.")]
    public async Task<string> LinkPostToMachines(
        [Description("Post id")] string id,
        [Description("Schema ids, separated by commas or spaces")] string schemaIds)
    {
        var ids = schemaIds
            .Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();
        if (ids.Count == 0) return "ERROR: no schema id given.";

        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        // MADE_FOR only goes from a post: the backend would reject a schema or a kit — say so before it does.
        if (p.ContentType != "POST_PROCESSOR")
            return $"ERROR: \"{p.Name}\" is not a post ({p.ContentType}); a \"made for\" link only goes from a post to a schema.";

        try { await dmc.AddLinks(p.Id, "MADE_FOR", ids, token); }
        catch (DmcHttpException e) when (e.Status == 400) { return "ERROR: DMC rejected the link — " + ErrorText(e.Body); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }

        IReadOnlyList<LinkInfo> links;
        try { links = await dmc.GetLinks(p.Id, token); }
        catch (Exception) { links = Array.Empty<LinkInfo>(); }
        var made = links.Where(l => l.LinkType == "MADE_FOR" && l.Product != null).Select(l => l.Product!.Name).ToList();
        return $"Post \"{p.Name}\" — made for: " + (made.Count > 0 ? string.Join(", ", made) : string.Join(", ", ids))
             + "\nLink: " + p.Url(dmc.Site);
    }

    // ----------------------------------------------------------------- list_my_posts

    internal static readonly string[] Statuses = { "DRAFT", "PENDING_REVIEW", "PUBLISHED", "REJECTED", "DISABLED", "ARCHIVED" };

    [McpServerTool(Name = "list_my_posts"), Description(
        "My posts in DMC with their statuses — what is not yet submitted for moderation, what is already in the catalogue.")]
    public async Task<string> ListMyPosts(
        [Description("Only this status: DRAFT, PENDING_REVIEW, PUBLISHED, REJECTED, DISABLED, ARCHIVED; empty for all")] string? status = null,
        [Description("Type: POST_PROCESSOR (default), MACHINE_SCHEMA, INTERPRETER, DIGITAL_MACHINE_KIT or ANY for all")] string? contentType = "POST_PROCESSOR")
    {
        string? want = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            want = status.Trim().ToUpperInvariant();
            if (!Statuses.Contains(want)) return $"ERROR: unknown status \"{want}\". Valid: " + string.Join(", ", Statuses);
        }
        var (type, typeErr) = NormalizeType(contentType);
        if (typeErr != null) return typeErr;
        var (token, err) = await Token();
        if (token == null) return err!;
        IReadOnlyList<ProductInfo> mine;
        try { mine = await dmc.MyProducts(token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }

        var posts = mine.Where(p => (type == null || p.ContentType == type) && (want == null || p.PublicationStatus == want)).ToList();
        if (posts.Count == 0) return want == null ? "You have no posts in DMC yet." : $"No posts with the status \"{StatusWord(want)}\".";
        var sb = new StringBuilder();
        foreach (var p in posts) sb.AppendLine(Line(p, own: false));
        sb.AppendLine("Total: " + string.Join(", ",
            posts.GroupBy(p => p.PublicationStatus).Select(g => $"{StatusWord(g.Key)}: {g.Count()}")));
        return sb.ToString();
    }

    // ------------------------------------------------------------------- delete_post

    [McpServerTool(Name = "delete_post"), Description(
        "Delete your own draft (or a rejected post) — for example, one uploaded by mistake. Does not delete what is " +
        "published or submitted for moderation: unpublishing is a deliberate step taken in your DMC account.")]
    public async Task<string> DeletePost(
        [Description("Post id")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        if (p.PublicationStatus is not ("DRAFT" or "REJECTED"))
            return $"ERROR: \"{p.Name}\" is {StatusWord(p.PublicationStatus)}; only a draft or a rejected post can be deleted. "
                 + "Unpublish it in your DMC account.";
        try { await dmc.DeleteProduct(p.Id, token); }
        catch (DmcHttpException e) when (e.Status == 409) { return "ERROR: DMC will not delete it — " + ErrorText(e.Body); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        return $"Draft \"{p.Name}\" deleted.";
    }

    // ----------------------------------------------------------------- AI on request

    [McpServerTool(Name = "generate_description"), Description(
        "Generate a post description with AI from the card's fields and save it (save=false only shows it).")]
    public async Task<string> GenerateDescription(
        [Description("Post id")] string id,
        [Description("true (default) — write it to the card; false — only return the text")] bool save = true)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        string text;
        try { text = await dmc.GenerateDescription(DmcJson.RequestFrom(p.Raw), token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: AI did not respond: " + e.Message; }
        if (!save) return text + "\n(not saved — save=false)";
        var (_, putErr) = await PutFields(p, new FieldSet(Description: text), token);
        return putErr ?? text + "\n(saved to the card)";
    }

    [McpServerTool(Name = "regenerate_cover"), Description(
        "A new post cover: archive — the picture from the component archive (no AI), ai — an AI render. Saved to the card.")]
    public async Task<string> RegenerateCover(
        [Description("Post id")] string id,
        [Description("archive (default) or ai")] string source = "archive")
    {
        var src = source.Trim().ToLowerInvariant();
        if (src is not ("archive" or "ai")) return "ERROR: the cover source must be archive or ai.";
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
                if (string.IsNullOrEmpty(file)) return "ERROR: the post has no archive to take a picture from.";
                var found = await dmc.ArchivePreview(file, token);
                if (found == null) return "The archive has no picture — try source=ai.";
                image = found;
            }
            else image = await dmc.GenerateImage(DmcJson.RequestFrom(p.Raw), token);
        }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: could not get the cover: " + e.Message; }

        var putErr = await PutRaw(p, "imageUrl", image, token);
        return putErr ?? $"Cover of \"{p.Name}\" updated (source: {(src == "archive" ? "archive" : "AI")}).\nLink: {p.Url(dmc.Site)}";
    }

    [McpServerTool(Name = "generate_sample_code"), Description(
        "Generate sample NC code for the post with AI and attach it to the card (Sample output code).")]
    public async Task<string> GenerateSampleCode(
        [Description("Post id")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        string path;
        try { path = await dmc.GenerateSampleCode(DmcJson.RequestFrom(p.Raw), token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: AI did not respond: " + e.Message; }
        var putErr = await PutRaw(p, "sampleOutputCodeFile", path, token);
        return putErr ?? $"Sample NC code generated and attached to \"{p.Name}\".\nLink: {p.Url(dmc.Site)}";
    }

    [McpServerTool(Name = "generate_codes_list"), Description(
        "Generate the list of the post's supported G/M codes with AI and attach it to the card (Supported codes).")]
    public async Task<string> GenerateCodesList(
        [Description("Post id")] string id)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token);
        if (p == null) return getErr!;
        string path;
        try { path = await dmc.GenerateCodesList(DmcJson.RequestFrom(p.Raw), token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: AI did not respond: " + e.Message; }
        var putErr = await PutRaw(p, "supportedCodesFile", path, token);
        return putErr ?? $"Codes list generated and attached to \"{p.Name}\".\nLink: {p.Url(dmc.Site)}";
    }

    /** The card by id — or a ready error text (404 means both "no such thing" and "someone else's draft"). */
    private async Task<(ProductInfo? Product, string? Error)> Get(string id, string token)
    {
        try
        {
            var p = await dmc.GetProduct(id, token);
            return p == null ? (null, Explain(new DmcHttpException(404, ""))) : (p, null);
        }
        catch (DmcHttpException e) { return (null, Explain(e)); }
        catch (Exception e) { return (null, "ERROR: DMC did not respond: " + e.Message); }
    }

    /** A full PUT with one changed field — for the cover and the files, which FieldSet does not have. */
    private async Task<string?> PutRaw(ProductInfo p, string field, string value, string token)
    {
        var body = DmcJson.RequestFrom(p.Raw);
        body[field] = value;
        try { await dmc.UpdateProduct(p.Id, body, token); return null; }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
    }

    // ------------------------------------------------------------------ check_import

    [McpServerTool(Name = "check_import"), Description(
        "Result of an import by importId — when publish_post or publish_folder gave up waiting and returned its id. " +
        "Does not re-send anything.")]
    public async Task<string> CheckImport(
        [Description("importId from the publish_post / publish_folder answer")] string importId,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        var (result, waitErr) = await WaitImport(importId.Trim(), token, progress);
        if (result == null) return waitErr!;
        var sb = new StringBuilder();
        await ReportComponents(sb, result, token);
        sb.AppendLine("Check the drafts (audit_drafts) and submit the ready ones — submit_drafts(\"ALL\").");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ audit_drafts

    [McpServerTool(Name = "audit_drafts"), Description(
        "Check your drafts against the moderation rules: what is ready to submit, what each one lacks " +
        "(name, machine maker, machine type, archive), which have no cover or description.")]
    public async Task<string> AuditDrafts()
    {
        var (token, err) = await Token();
        if (token == null) return err!;
        IReadOnlyList<ProductInfo> mine;
        try { mine = await dmc.MyProducts(token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }

        var drafts = mine.Where(p => p.PublicationStatus == "DRAFT").ToList();
        if (drafts.Count == 0) return "No drafts.";
        var sb = new StringBuilder();
        int ready = 0;
        foreach (var p in drafts)
        {
            var missing = Missing(p);
            var soft = new List<string>();
            if (!p.HasCover) soft.Add("no cover");
            if (string.IsNullOrWhiteSpace(p.Description)) soft.Add("no description");
            if (missing.Count == 0)
            {
                ready++;
                sb.AppendLine($"{Title(p)} — ready to submit" + (soft.Count > 0 ? $" ({string.Join(", ", soft)})" : ""));
            }
            else
                sb.AppendLine($"{Title(p)} — missing: {string.Join(", ", missing)}" + (soft.Count > 0 ? $"; {string.Join(", ", soft)}" : ""));
        }
        sb.AppendLine($"Ready: {ready}, not ready: {drafts.Count - ready}. Submit the ready ones — submit_drafts(\"ALL\").");
        return sb.ToString();
    }

    // ----------------------------------------------------------------- submit_drafts

    [McpServerTool(Name = "submit_drafts"), Description(
        "Submit several drafts for moderation: ids separated by commas, or ALL for all your ready ones. " +
        "Lists the ones that are not ready, with what they lack.")]
    public async Task<string> SubmitDrafts(
        [Description("Ids separated by commas, or ALL")] string ids)
    {
        var (token, err) = await Token();
        if (token == null) return err!;

        List<ProductInfo> targets;
        if (ids.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<ProductInfo> mine;
            try { mine = await dmc.MyProducts(token); }
            catch (DmcHttpException e) { return Explain(e); }
            catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
            targets = mine.Where(p => p.PublicationStatus == "DRAFT").ToList();
            if (targets.Count == 0) return "You have no drafts.";
        }
        else
        {
            var list = ids.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().ToList();
            if (list.Count == 0) return "ERROR: no id given.";
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
                sb.AppendLine($"{Title(p)} — already {StatusWord(p.PublicationStatus)}");
                continue;
            }
            var missing = Missing(p);
            if (missing.Count > 0) { sb.AppendLine($"{Title(p)} — missing: {string.Join(", ", missing)}"); continue; }
            try { await dmc.SetStatus(p.Id, "PENDING_REVIEW", token); sb.AppendLine($"{Title(p)} — submitted for moderation"); }
            catch (DmcHttpException e) when (e.Status == 400) { sb.AppendLine($"{Title(p)} — DMC did not accept it: {ErrorText(e.Body)}"); }
            catch (DmcHttpException e) { sb.AppendLine($"{Title(p)} — {Explain(e)}"); }
            catch (Exception e) { sb.AppendLine($"{Title(p)} — DMC did not respond: {e.Message}"); }
        }
        return sb.ToString();
    }

    // ----------------------------------------------------------------- describe_post

    [McpServerTool(Name = "describe_post"), Description(
        "The full card of a component: the whole description, control and machine, cover, files, price and trial, " +
        "links — to judge what AI filled in. Takes an id or a slug.")]
    public async Task<string> DescribePost(
        [Description("Id or slug")] string idOrSlug)
    {
        var (token, _) = await Token(); // published ones are visible without sign-in too
        ProductInfo? p;
        try { p = await dmc.GetProduct(idOrSlug, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: DMC did not respond: " + e.Message; }
        if (p == null) return $"DMC did not find \"{idOrSlug}\" — it does not exist, or it is someone else's draft.";

        var sb = new StringBuilder();
        sb.AppendLine($"{p.Name} [{p.ContentType}] — {StatusWord(p.PublicationStatus)}");
        sb.AppendLine($"Link: {p.Url(dmc.Site)}   id: {p.Id}");
        sb.AppendLine($"Control: {p.Controller}");
        sb.AppendLine($"Machine: {p.Machine}" + (p.MachineType != null ? $", type {p.MachineType}" : "")
                      + (p.NumberOfAxes is int n ? $", {n} axes" : ""));
        sb.AppendLine("Description: " + (string.IsNullOrWhiteSpace(p.Description) ? "none" : p.Description));
        sb.AppendLine("Cover: " + (RawStr(p, "imageUrl") ?? "none"));
        sb.AppendLine("Archive: " + (RawStr(p, "productFile") ?? "none"));
        sb.AppendLine("Sample code: " + (RawStr(p, "sampleOutputCodeFile") ?? "none")
                      + "; codes list: " + (RawStr(p, "supportedCodesFile") ?? "none"));
        var price = RawNum(p, "priceEur");
        var trial = RawNum(p, "trialDays");
        sb.AppendLine("Price: " + (price == null ? "not set" : price == -1 ? "included in maintenance" : price == 0 ? "free" : $"{price} €")
                      + (trial != null ? $", trial {trial} days" : ""));
        IReadOnlyList<LinkInfo> links;
        try { links = await dmc.GetLinks(p.Id, token); }
        catch (Exception) { links = Array.Empty<LinkInfo>(); }
        var named = links.Where(l => l.Product != null).Select(l => $"{l.Product!.Name} ({l.LinkType})").ToList();
        sb.AppendLine("Links: " + (named.Count == 0 ? "none" : string.Join(", ", named)));
        return sb.ToString();
    }

    // ------------------------------------------------ inspect_archive / set_cover

    [McpServerTool(Name = "inspect_archive"), Description(
        "What is inside a component archive — locally, no server and no AI: machine name, axes and travels, " +
        "equipment, picture, posts, controls mentioned. From these facts you can write the description yourself " +
        "and fill in the fields with update_post — when the server AI is not wanted (publish_post with ai=false).")]
    public Task<string> InspectArchive(
        [Description("Path to a .zip / .sppx / .dll / .stnci")] string file) =>
        Task.FromResult(ArchiveInspector.Inspect(file));

    internal static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

    [McpServerTool(Name = "set_cover"), Description(
        "Set your own cover — a picture from the author or from the agent: the file goes to storage and is put " +
        "on the card. The server AI is not involved.")]
    public async Task<string> SetCover(
        [Description("Component id")] string id,
        [Description("Path to the picture: .png, .jpg or .webp")] string file)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ERROR: file {path} does not exist.";
        if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"ERROR: {Path.GetFileName(path)} is not a picture. Expected .png, .jpg or .webp.";

        var (token, err) = await Token();
        if (token == null) return err!;
        var (p, getErr) = await Get(id, token); // read the card first: someone else's id must not cost a file in tmp/
        if (p == null) return getErr!;

        string staged;
        try { staged = await dmc.UploadFile(path, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ERROR: could not upload the file to DMC: " + e.Message; }

        var putErr = await PutRaw(p, "imageUrl", staged, token);
        return putErr ?? $"Cover of \"{p.Name}\" replaced with {Path.GetFileName(path)}.\nLink: {p.Url(dmc.Site)}";
    }

    // ---------------------------------------------------- readiness and similar ones

    /** What is missing for moderation — the same four rules as the backend's updateStatus. */
    internal static List<string> Missing(ProductInfo p)
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name)) m.Add("name");
        if (string.IsNullOrWhiteSpace(p.MachineManufacturer)) m.Add("machine maker");
        if (p.MachineType == null) m.Add("machine type");
        if (string.IsNullOrWhiteSpace(RawStr(p, "productFile"))) m.Add("archive");
        return m;
    }

    private static string Title(ProductInfo p) =>
        string.IsNullOrWhiteSpace(p.Name) ? $"(no name, id {p.Id})" : $"{p.Name} (id {p.Id})";

    internal static string? RawStr(ProductInfo p, string key) =>
        p.Raw.ValueKind == JsonValueKind.Object && p.Raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static decimal? RawNum(ProductInfo p, string key) =>
        p.Raw.ValueKind == JsonValueKind.Object && p.Raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal() : null;

    /**
     * Similar by name — published ones from the catalogue and your own in any status. A name counts as similar
     * when it contains the query or the query contains it: "Fanuc 0i" finds "Fanuc 0i for Haas VF-2". A failure of
     * either source does not break the guard — the upload matters more than the lookup.
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
        catch (Exception) { /* the catalogue did not respond — check at least your own */ }
        try
        {
            foreach (var p in await dmc.MyProducts(token))
                if (Like(p, q) && hits.All(h => h.P.Id != p.Id)) hits.Add((p, true));
        }
        catch (Exception) { /* your own could not be read */ }
        return hits;
    }

    private string SimilarText(List<(ProductInfo P, bool Own)> hits)
    {
        var sb = new StringBuilder("Similar ones already exist:\n");
        foreach (var (p, own) in hits) sb.AppendLine(Line(p, own));
        sb.AppendLine("Repeat with force=true to upload anyway, or update the existing one: update_post / replace_post_file.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- import: common

    /**
     * Sends the file to bulk-zip and waits for the import to finish. Returns the progress, or a ready error
     * text — including a timeout, when the server carries on without us.
     */
    internal async Task<(ImportProgress? Progress, string? Error)> Import(string path, bool ai, string? nameHint,
        string? descriptionHint, string token, IProgress<ProgressNotificationValue>? progress = null)
    {
        string importId = Guid.NewGuid().ToString("N");
        try { importId = await dmc.StartImport(path, importId, ai, nameHint, descriptionHint, token); }
        catch (DmcHttpException e) { return (null, Explain(e)); }
        catch (Exception e) { return (null, "ERROR: could not send the file to DMC: " + e.Message); }
        return await WaitImport(importId, token, progress);
    }

    /**
     * Waits for the import with importId to finish: the progress — or a ready text (timeout, server error,
     * someone else's id). Progress also goes to the editor if it sent a progressToken: otherwise, through minutes
     * of silence, there is no telling whether the tool is alive.
     */
    internal async Task<(ImportProgress? Progress, string? Error)> WaitImport(string importId, string token,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ImportProgress state;
        while (true)
        {
            try { state = await dmc.GetImportProgress(importId, token); }
            catch (DmcHttpException e) when (e.Status == 404) { return (null, $"ERROR: import {importId} not found — old or someone else's."); }
            catch (DmcHttpException e) { return (null, Explain(e)); }
            catch (Exception e) { return (null, $"ERROR: DMC did not respond about import {importId}: {e.Message}"); }
            progress?.Report(new ProgressNotificationValue
            {
                Progress = state.Done,
                Total = state.Total > 0 ? state.Total : null,
                Message = state.Finished ? "done" : $"{state.Status}: {state.CurrentName}",
            });
            if (state.Finished) break;
            // "queued" is a queue behind someone else's import, not a hang; wait the same way.
            if (sw.Elapsed >= MaxWait)
                return (null, $"Import {importId} is still running (now: {state.Status}, {state.CurrentName}). "
                            + "The server carries on — the draft will show up in your DMC account under \"My components\", "
                            + $"and check_import(\"{importId}\") will show the result. Do not send the file again.");
            await Delay(PollEvery);
        }

        if (state.Status == "error") return (null, "ERROR: import failed — " + (state.StatusReason ?? "no reason given"));
        if (state.Status == "cancelled") return (null, "The import was cancelled on the server.");
        if (state.Components.Count == 0)
            return (null, "ERROR: DMC did not find a component in the file."
                        + (state.Errors.Count > 0 ? "\n" + string.Join("\n", state.Errors) : ""));
        return (state, null);
    }

    /** Report lines for every created draft and for what was not accepted. */
    internal async Task ReportComponents(StringBuilder sb, ImportProgress progress, string token)
    {
        foreach (var c in progress.Components)
        {
            sb.AppendLine($"Draft created: {c.Name} ({c.ContentType})" + (c.AiEnriched ? ", fields filled in by AI" : ""));
            ProductInfo? p = null;
            try { p = await dmc.GetProduct(c.ProductId, token); }
            catch (Exception) { /* the card could not be read — at least the id goes out below */ }
            if (p != null)
            {
                sb.AppendLine(Describe(p));
                sb.AppendLine(p.HasCover ? "Cover: yes" : "Cover: no");
                sb.AppendLine(string.IsNullOrWhiteSpace(p.Description) ? "Description: no" : "Description: yes");
            }
            sb.AppendLine($"id: {c.ProductId}");
        }
        foreach (var e in progress.Errors) sb.AppendLine("Not accepted: " + e);
    }

    // ---------------------------------------------------------------- fields: common

    /** The card fields the tools change. Null means "leave alone". */
    internal sealed record FieldSet(string? Name = null, string? Description = null,
        string? ControllerManufacturer = null, string? ControllerSeries = null, string? ControllerModel = null,
        string? MachineManufacturer = null, string? MachineSeries = null, string? MachineModel = null,
        string? MachineType = null, int? NumberOfAxes = null,
        double? TravelXMm = null, double? TravelYMm = null, double? TravelZMm = null)
    {
        public bool IsEmpty => Name == null && Description == null && ControllerManufacturer == null
            && ControllerSeries == null && ControllerModel == null && MachineManufacturer == null
            && MachineSeries == null && MachineModel == null && MachineType == null && NumberOfAxes == null
            && TravelXMm == null && TravelYMm == null && TravelZMm == null;
    }

    /** The largest travel that still looks like a machine rather than a typo (100 m). */
    private const double MaxTravelMm = 100_000;

    /** Machine type — upper-cased and checked against the backend's list; axes — within 1..12; travel — positive. */
    internal static (FieldSet? Fields, string? Error) Normalize(FieldSet f)
    {
        if (f.MachineType != null)
        {
            var mt = f.MachineType.Trim().ToUpperInvariant();
            if (!MachineTypes.Contains(mt))
                return (null, $"ERROR: unknown machine type \"{mt}\". Valid: " + string.Join(", ", MachineTypes));
            f = f with { MachineType = mt };
        }
        if (f.NumberOfAxes is < 1 or > 12) return (null, "ERROR: the number of axes must be from 1 to 12.");
        foreach (var (axis, travel) in new[] { ("X", f.TravelXMm), ("Y", f.TravelYMm), ("Z", f.TravelZMm) })
            if (travel is double t && (t <= 0 || t > MaxTravelMm || double.IsNaN(t)))
                return (null, $"ERROR: the {axis}-axis travel is {t.ToString(System.Globalization.CultureInfo.InvariantCulture)} mm; "
                              + $"it must be a number above zero and no more than {MaxTravelMm:0} mm.");
        return (f, null);
    }

    /**
     * A full PUT: the body is the whole current card with only the passed fields on top. Returns a list of
     * "field: before → after" (empty when nothing changed and no PUT was made) or an error text.
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
        // Axis travels: ProductInfo does not carry them — the old value comes straight from the card.
        void SetTravel(string field, double? value)
        {
            if (value is not double v) return;
            double? old = p.Raw.ValueKind == JsonValueKind.Object && p.Raw.TryGetProperty(field, out var e)
                          && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
            if (old is double o && Math.Abs(o - v) < 1e-6) return;
            body[field] = v;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            changes.Add($"{field}: {old?.ToString(inv) ?? "—"} → {v.ToString(inv)}");
        }
        SetTravel("travelXMm", f.TravelXMm);
        SetTravel("travelYMm", f.TravelYMm);
        SetTravel("travelZMm", f.TravelZMm);
        if (changes.Count == 0) return (changes, null);

        try { await dmc.UpdateProduct(p.Id, body, token); }
        catch (DmcHttpException e) { return (changes, Explain(e)); }
        catch (Exception e) { return (changes, "ERROR: DMC did not respond: " + e.Message); }
        return (changes, null);
    }

    // ------------------------------------------------------------------------ common

    internal string Describe(ProductInfo p)
    {
        var line = $"{p.Name} — {StatusWord(p.PublicationStatus)}\nLink: {p.Url(dmc.Site)}\n"
                 + $"Control: {p.Controller}\nMachine: {p.Machine}";
        if (p.MachineType != null) line += $", type {p.MachineType}";
        if (p.NumberOfAxes is int n) line += $", {n} axes";
        return line;
    }

    internal static string StatusWord(string s) => s switch
    {
        "DRAFT" => "draft",
        "PENDING_REVIEW" => "pending moderation",
        "PUBLISHED" => "published",
        "REJECTED" => "rejected",
        "DISABLED" => "disabled",
        "ARCHIVED" => "archived",
        _ => s,
    };

    internal static string Explain(DmcHttpException e) => e.Status switch
    {
        401 => NoLogin,
        403 => "ERROR: you need the publisher role in DMC — ask an administrator for it.",
        404 => "ERROR: DMC did not find this component — or it is not yours.",
        409 => "ERROR: you already have an import running in DMC — wait for it and try again. (" + ErrorText(e.Body) + ")",
        _ => $"ERROR: DMC returned {e.Status}: {ErrorText(e.Body)}",
    };

    /** The backend sends errors as {"error":"..."} — show the text, not the JSON. */
    internal static string ErrorText(string body)
    {
        try
        {
            var r = JsonDocument.Parse(body).RootElement;
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString()!;
        }
        catch (JsonException) { /* not JSON — return it as is */ }
        return body.Length > 300 ? body[..300] : body;
    }

    private async Task<(string? Token, string? Error)> Token()
    {
        try
        {
            var t = await tokens.GetAccessToken();
            return (t, t == null ? NoLogin : null);
        }
        catch (InvalidOperationException e) { return (null, "ERROR: " + e.Message); }
    }
}
