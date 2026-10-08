using System.IO.Compression;
using DmcMcp;
using Xunit;

/** What a tool's file argument may be: a local path at home, an upload id or a link on the hosted server. */
public class FileInputsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-fi-" + Guid.NewGuid().ToString("N")[..8]);
    public FileInputsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UploadStore Uploads() => new(Path.Combine(_dir, "uploads"));
    private FileInputs Hosted() => new(true, Uploads(), tempRoot: Path.Combine(_dir, "tmp"));

    [Fact]
    public async Task LocalPathWorksAtHome()
    {
        var f = Path.Combine(_dir, "a.sppx");
        File.WriteAllText(f, "x");
        using var input = await FileInputs.Local.File(f);
        Assert.Null(input.Error);
        Assert.Equal(f, input.Path);
    }

    [Fact]
    public async Task MissingLocalFileIsReportedAtHome()
    {
        using var input = await FileInputs.Local.File(Path.Combine(_dir, "nope.sppx"));
        Assert.StartsWith("ERROR: file", input.Error);
    }

    /** /proc/self/environ would carry the key: the hosted server reads no path a caller names. */
    [Theory]
    [InlineData("/proc/self/environ")]
    [InlineData("/data/dmc-mcp/auth.json")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("a.sppx")]
    public async Task HostedServerRefusesPaths(string path)
    {
        using var input = await Hosted().File(path);
        Assert.Null(input.Path);
        Assert.Equal(FileInputs.HostedPathRefusal, input.Error);
    }

    [Fact]
    public async Task UploadIdResolvesOnTheHostedServer()
    {
        using var asTheKey = Caller.Enter(Caller.Server); // the door's doing on the hosted server
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        using var input = await Hosted().File(saved.Ref);
        Assert.Null(input.Error);
        Assert.Equal("a.sppx", Path.GetFileName(input.Path));
    }

    /** No caller — a request that has ended — owns nothing: not even the key's uploads resolve. */
    [Fact]
    public async Task WithoutACallerNoUploadResolves()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        using var input = await Hosted().File(saved.Ref);
        Assert.Null(input.Path);
        Assert.Contains("upload the file again", input.Error);
    }

    [Fact]
    public async Task UnknownUploadIdSaysUploadAgain()
    {
        using var input = await Hosted().File("upload:0123456789abcdef0123456789abcdef");
        Assert.Contains("upload the file again", input.Error);
    }

    [Fact]
    public async Task UploadIdAtHomeExplainsItself()
    {
        using var input = await FileInputs.Local.File("upload:0123456789abcdef0123456789abcdef");
        Assert.Contains("only on the hosted server", input.Error);
    }

    [Fact]
    public async Task UploadedZipBecomesTheFolderAndGoesAway()
    {
        using var asTheKey = Caller.Enter(Caller.Server);
        var zip = Path.Combine(_dir, "f.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("a.sppx").Open())) w.Write("x");
        var saved = await Uploads().Save(File.OpenRead(zip), "f.zip");
        string? folder;
        using (var input = await Hosted().Folder(saved.Ref))
        {
            Assert.Null(input.Error);
            folder = input.Path;
            Assert.True(File.Exists(Path.Combine(folder!, "a.sppx")));
        }
        Assert.False(Directory.Exists(folder));
    }

    /** Whatever goes wrong while unpacking, the tool answers ERROR and the temp folder does not stay on the disk. */
    [Fact]
    public async Task AZipThatCannotBeUnpackedLeavesNothing()
    {
        using var asTheKey = Caller.Enter(Caller.Server);
        var zip = Path.Combine(_dir, "bad.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var name in new[] { "big.bin", "x", "x/y" })
                using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write("x");
        var saved = await Uploads().Save(File.OpenRead(zip), "bad.zip");
        using var input = await Hosted().Folder(saved.Ref);
        Assert.StartsWith("ERROR:", input.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "tmp")));
    }

    /** A link that fails in a way the downloader did not expect still answers ERROR and leaves no temp folder. */
    [Fact]
    public async Task AFailedDownloadLeavesNothing()
    {
        using var asTheKey = Caller.Enter(Caller.Server);
        var inputs = new FileInputs(true, Uploads(), new Downloader(new Throwing()), Path.Combine(_dir, "tmp"));
        using var input = await inputs.File("https://files.example.com/post.sppx");
        Assert.StartsWith("ERROR:", input.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "tmp")));
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("something nobody expected");
    }

    [Fact]
    public async Task HostedFolderPathIsRefused()
    {
        using var input = await Hosted().Folder("/data");
        Assert.Equal(FileInputs.HostedPathRefusal, input.Error);
    }

    // ------------------------------------------------------------- the server's own disk (0.9.0)

    private static readonly Caller Anna = Caller.Person("tok-anna", "anna@example.com", TestIssuer.Issuer, "sub-anna");
    private static readonly Caller Boris = Caller.Person("tok-boris", "boris@example.com", TestIssuer.Issuer, "sub-boris");

    private static FakeDmcClient Publisher() => new() { Who = new MeInfo("anna@example.com", new[] { "USER", "DEALER" }, "acc", null) };
    private static FakeDmcClient Customer() => new() { Who = new MeInfo("anna@example.com", new[] { "USER" }, "acc", null) };

    private FileInputs Gated(HttpMessageHandler files, FakeDmcClient dmc, out DiskGate gate)
    {
        gate = new DiskGate(new Who(dmc));
        return new FileInputs(true, Uploads(), new Downloader(files), Path.Combine(_dir, "tmp"), gate);
    }

    private static async Task<T> As<T>(Caller caller, Func<Task<T>> call)
    {
        using (Caller.Enter(caller)) return await call();
    }

    private string[] Temp() =>
        Directory.Exists(Path.Combine(_dir, "tmp")) ? Directory.GetFileSystemEntries(Path.Combine(_dir, "tmp")) : Array.Empty<string>();

    /** inspect_archive asks DMC nothing itself: the gate is what keeps a licsys account from filling the disk with links. */
    [Fact]
    public async Task ALinkIsNotFetchedForSomeoneDmcDoesNotLetPublish()
    {
        var files = new Files();
        var inputs = Gated(files, Customer(), out _);
        using var input = await As(Anna, () => inputs.File("https://files.example.com/post.sppx"));
        Assert.Null(input.Path);
        Assert.Contains("not a Publisher", input.Error);
        Assert.Equal(0, files.Calls);
        Assert.Empty(Temp());
    }

    [Fact]
    public async Task ALinkIsFetchedForAPublisher()
    {
        var files = new Files();
        var inputs = Gated(files, Publisher(), out _);
        using var input = await As(Anna, () => inputs.File("https://files.example.com/post.sppx"));
        Assert.Null(input.Error);
        Assert.True(File.Exists(input.Path));
    }

    /** No caller — the request has ended — and no download: nobody is there to use it, nor to count it against. */
    [Fact]
    public async Task WithoutACallerNoLinkIsFetched()
    {
        var files = new Files();
        var inputs = Gated(files, Publisher(), out _);
        using var input = await inputs.File("https://files.example.com/post.sppx");
        Assert.StartsWith("ERROR:", input.Error);
        Assert.Equal(0, files.Calls);
    }

    [Fact(Timeout = 15000)]
    public async Task APersonFetchesOneLinkAtATime()
    {
        var files = new Files { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var inputs = Gated(files, Publisher(), out _);
        var first = As(Anna, () => inputs.File("https://files.example.com/a.sppx"));
        await files.Arrived.WaitAsync();
        using (var second = await As(Anna, () => inputs.File("https://files.example.com/b.sppx")))
            Assert.Contains("you already have a download or an unpacking running", second.Error);
        var boris = As(Boris, () => inputs.File("https://files.example.com/c.sppx")); // someone else's goes ahead
        await files.Arrived.WaitAsync();
        files.Hold.SetResult();
        using (var a = await first) Assert.Null(a.Error);
        using (var b = await boris) Assert.Null(b.Error);
        Assert.Equal(2, files.Calls);
    }

    /** Unpacking an upload takes the same slot as a download: a zip of a gigabyte costs the disk as much. */
    [Fact]
    public async Task UnpackingTakesTheSameSlot()
    {
        var inputs = Gated(new Files(), Publisher(), out var gate);
        var zip = Path.Combine(_dir, "f.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("a.sppx").Open())) w.Write("x");
        UploadStore.Saved saved;
        await using (var content = File.OpenRead(zip)) saved = await Uploads().Save(content, "f.zip", Anna.Owner);
        using (gate.TryBegin(Anna, out _))
        using (var busy = await As(Anna, () => inputs.Folder(saved.Ref)))
            Assert.Contains("you already have a download or an unpacking running", busy.Error);
        using var unpacked = await As(Anna, () => inputs.Folder(saved.Ref));
        Assert.Null(unpacked.Error);
    }

    /** A file server that counts who asked and, while Hold is set, answers once it is let go. */
    private sealed class Files : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource? Hold { get; init; }
        public SemaphoreSlim Arrived { get; } = new(0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Arrived.Release();
            if (Hold != null) await Hold.Task.WaitAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
                RequestMessage = request,
            };
        }
    }
}
