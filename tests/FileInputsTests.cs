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
}
