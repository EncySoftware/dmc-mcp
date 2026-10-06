namespace DmcMcp;

/// <summary>
/// Turns a tool's file argument into a local path. At home that is the path itself (an https link works too). On
/// the hosted server it is never a path — the server would be reading its own disk, and /proc/self/environ holds
/// its key — but an upload:&lt;id&gt; from POST /mcp/upload or an https link. A folder (publish_folder) arrives on
/// the hosted server as a zip. Temporary copies go away when the Input is disposed.
/// </summary>
public sealed class FileInputs(bool hosted, UploadStore? uploads = null, Downloader? downloader = null, string? tempRoot = null)
{
    public static FileInputs Local { get; } = new(false);

    internal const string HostedPathRefusal =
        "ERROR: this is the hosted DMC server — it cannot read paths, neither yours nor its own. Upload the file " +
        "first (POST <server>/mcp/upload with the same key, multipart field \"file\") and pass the upload:<id> it " +
        "returns, or pass an https:// link. A folder goes as a zip.";

    private readonly Downloader _downloader = downloader ?? new Downloader();
    private readonly string _temp = tempRoot ?? Path.Combine(Path.GetTempPath(), "dmc-mcp");

    public bool Hosted => hosted;

    public sealed class Input : IDisposable
    {
        internal Input(string? path, string? error, string? cleanup = null) { Path = path; Error = error; _cleanup = cleanup; }
        private readonly string? _cleanup;
        public string? Path { get; }
        public string? Error { get; }
        public void Dispose()
        {
            if (_cleanup == null) return;
            try { Directory.Delete(_cleanup, true); } catch { /* best effort: the OS temp cleaner takes the rest */ }
        }
    }

    public async Task<Input> File(string arg, CancellationToken ct = default)
    {
        arg = arg.Trim();
        if (arg.StartsWith("upload:", StringComparison.OrdinalIgnoreCase))
        {
            if (uploads == null)
                return new Input(null, "ERROR: upload:<id> works only on the hosted server — here pass the file's path.");
            var stored = uploads.Resolve(arg);
            return stored == null
                ? new Input(null, $"ERROR: {arg} is unknown or expired (uploads live 24 hours) — upload the file again.")
                : new Input(stored, null);
        }
        if (arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var dir = NewTemp();
            try
            {
                var (path, error) = await _downloader.Fetch(arg, dir, ct);
                return path == null ? Cleaned(error!, dir) : new Input(path, null, dir);
            }
            catch (Exception e)
            {
                // Whatever the downloader did not foresee: the half-written file must not stay on the server's disk.
                Cleaned("", dir);
                if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
                return new Input(null, "ERROR: could not download the link — " + e.Message);
            }
        }
        if (hosted) return new Input(null, HostedPathRefusal);
        var full = System.IO.Path.GetFullPath(arg);
        return System.IO.File.Exists(full) ? new Input(full, null) : new Input(null, $"ERROR: file {full} does not exist.");
    }

    public async Task<Input> Folder(string arg, CancellationToken ct = default)
    {
        arg = arg.Trim();
        var reference = arg.StartsWith("upload:", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!reference)
        {
            if (hosted) return new Input(null, HostedPathRefusal);
            var full = System.IO.Path.GetFullPath(arg);
            return Directory.Exists(full) ? new Input(full, null) : new Input(null, $"ERROR: folder {full} does not exist.");
        }
        using var zip = await File(arg, ct);
        if (zip.Error != null) return new Input(null, zip.Error);
        var dir = NewTemp();
        try
        {
            var (root, error) = ZipFolder.Extract(zip.Path!, dir);
            return root == null ? Cleaned(error!, dir) : new Input(root, null, dir);
        }
        catch
        {
            Cleaned("", dir);
            throw;
        }
    }

    private string NewTemp()
    {
        var dir = System.IO.Path.Combine(_temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Input Cleaned(string error, string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
        return new Input(null, error);
    }
}
