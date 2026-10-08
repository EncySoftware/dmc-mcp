namespace DmcMcp;

/// <summary>
/// Turns a tool's file argument into a local path. At home that is the path itself (an https link works too). On
/// the hosted server it is never a path — the server would be reading its own disk, and /proc/self/environ holds
/// its key — but an upload:&lt;id&gt; from POST /mcp/upload (the caller's own: someone else's resolves to nothing) or
/// an https link. A folder (publish_folder) arrives on the hosted server as a zip. On the hosted server a link is
/// downloaded, and a zip unpacked, only for a caller the <see cref="DiskGate"/> lets have files kept there, and in
/// one of its slots, held while the download or the unpacking runs. Temporary copies go away when the Input is
/// disposed.
/// </summary>
public sealed class FileInputs(bool hosted, UploadStore? uploads = null, Downloader? downloader = null, string? tempRoot = null,
    DiskGate? gate = null)
{
    public static FileInputs Local { get; } = new(false);

    internal const string HostedPathRefusal =
        "ERROR: this is the hosted DMC server — it cannot read paths, neither yours nor its own. Upload the file " +
        "first (POST <server>/mcp/upload with the same key or token, multipart field \"file\") and pass the upload:<id> it " +
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
            // Best effort: what stays is swept by UploadJanitor on the hosted server, by the OS temp cleaner at home.
            try { Directory.Delete(_cleanup, true); } catch { }
        }
    }

    public async Task<Input> File(string arg, CancellationToken ct = default)
    {
        arg = arg.Trim();
        if (IsUpload(arg)) return Stored(arg);
        if (IsLink(arg))
        {
            var (slot, refusal) = await Admit();
            if (refusal != null) return new Input(null, refusal);
            using (slot) return await Download(arg, ct);
        }
        if (hosted) return new Input(null, HostedPathRefusal);
        var full = System.IO.Path.GetFullPath(arg);
        return System.IO.File.Exists(full) ? new Input(full, null) : new Input(null, $"ERROR: file {full} does not exist.");
    }

    public async Task<Input> Folder(string arg, CancellationToken ct = default)
    {
        arg = arg.Trim();
        if (!IsUpload(arg) && !IsLink(arg))
        {
            if (hosted) return new Input(null, HostedPathRefusal);
            var full = System.IO.Path.GetFullPath(arg);
            return Directory.Exists(full) ? new Input(full, null) : new Input(null, $"ERROR: folder {full} does not exist.");
        }
        // An unknown upload (or someone else's) is said before a slot is taken; a link is fetched in the same slot it
        // is unpacked in.
        var stored = IsUpload(arg) ? Stored(arg) : null;
        if (stored?.Error != null) return stored;
        var (slot, refusal) = await Admit();
        if (refusal != null) return new Input(null, refusal);
        using (slot)
        {
            using var zip = stored ?? await Download(arg, ct);
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
    }

    private static bool IsUpload(string arg) => arg.StartsWith("upload:", StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string arg) =>
        arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /** upload:&lt;id&gt; — the caller's own; someone else's, like an unknown one, is not found. */
    private Input Stored(string arg)
    {
        if (uploads == null)
            return new Input(null, "ERROR: upload:<id> works only on the hosted server — here pass the file's path.");
        // Whoever the door let in: the key's account or a person. No caller — the request is over — owns nothing.
        var stored = Caller.Current is { } caller ? uploads.Resolve(arg, caller.Owner) : null;
        return stored == null
            ? new Input(null, $"ERROR: {arg} is unknown or expired (uploads live 24 hours) — upload the file again.")
            : new Input(stored, null);
    }

    /**
     * On the hosted server, before anything lands on its disk: a caller (work whose request has ended has none and
     * gets nothing), one the gate lets have files kept, and a free slot. At home there is nobody to ask.
     */
    private async Task<(IDisposable? Slot, string? Refusal)> Admit()
    {
        if (!hosted) return (null, null);
        if (Caller.Current is not { } caller) return (null, DmcTools.RequestEnded);
        if (gate == null) return (null, null);
        if (await gate.Check(caller) is { } refused) return (null, "ERROR: " + refused.Message);
        var slot = gate.TryBegin(caller, out var busy);
        return slot == null ? (null, "ERROR: " + busy) : (slot, null);
    }

    private async Task<Input> Download(string url, CancellationToken ct)
    {
        var dir = NewTemp();
        try
        {
            var (path, error) = await _downloader.Fetch(url, dir, ct);
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
