using DmcMcp;
using Xunit;

/** The same tools on the hosted server: paths refused, upload ids accepted, sign-in hint names the container. */
public class HostedToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-ht-" + Guid.NewGuid().ToString("N")[..8]);
    public HostedToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UploadStore Uploads() => new(Path.Combine(_dir, "uploads"));
    private DmcTools Tools(FakeDmcClient client, string? token = "tok") => Tools(client, new FakeTokens(token));
    private DmcTools Tools(FakeDmcClient client, DmcTokenProvider tokens) =>
        new(client, tokens, new FileInputs(true, Uploads(), tempRoot: Path.Combine(_dir, "tmp")))
        {
            Delay = _ => Task.CompletedTask,
            PollEvery = TimeSpan.Zero,
        };

    [Fact]
    public async Task PublishPostRefusesAServerPath()
    {
        var answer = await Tools(new FakeDmcClient()).PublishPost("/proc/self/environ");
        Assert.Equal(FileInputs.HostedPathRefusal, answer);
    }

    [Fact]
    public async Task InspectArchiveRefusesAServerPath()
    {
        var answer = await Tools(new FakeDmcClient()).InspectArchive("/etc/passwd");
        Assert.Equal(FileInputs.HostedPathRefusal, answer);
    }

    [Fact]
    public async Task SetCoverRefusesAServerPath()
    {
        var answer = await Tools(new FakeDmcClient()).SetCover("some-id", "/data/dmc-mcp/auth.json");
        Assert.Equal(FileInputs.HostedPathRefusal, answer);
    }

    [Fact]
    public async Task ReplacePostFileRefusesAServerPath()
    {
        var answer = await Tools(new FakeDmcClient()).ReplacePostFile("some-id", "/etc/hostname");
        Assert.Equal(FileInputs.HostedPathRefusal, answer);
    }

    [Fact]
    public async Task PublishFolderRefusesAServerPath()
    {
        var answer = await Tools(new FakeDmcClient()).PublishFolder("/data", dryRun: true);
        Assert.Equal(FileInputs.HostedPathRefusal, answer);
    }

    [Fact]
    public async Task UploadedPostIsImportedFromTheStore()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1, 2 }), "Fanuc.sppx");
        var client = new FakeDmcClient();
        client.Progress.Enqueue(FakeDmcClient.Done(new ImportComponent("Fanuc", "POST_PROCESSOR", "p1", AiEnriched: false)));
        await Tools(client).PublishPost(saved.Ref, force: true);
        var start = Assert.Single(client.Starts);
        Assert.Equal("Fanuc.sppx", Path.GetFileName(start.File));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_dir, "uploads")), Path.GetFullPath(start.File));
    }

    [Fact]
    public async Task NotSignedInNamesTheContainerCommand()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        var answer = await Tools(new FakeDmcClient(), token: null).PublishPost(saved.Ref);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, answer);
    }

    /** A lost offline session (idle too long, revoked) points at the container command too, not at `dmc-mcp login`. */
    [Fact]
    public async Task ExpiredSignInNamesTheContainerCommand()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        var answer = await Tools(new FakeDmcClient(), new ExpiredTokens()).PublishPost(saved.Ref);
        Assert.StartsWith("ERROR", answer);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, answer);
    }

    private sealed class ExpiredTokens : DmcTokenProvider
    {
        public override Task<string?> GetAccessToken() => Task.FromException<string?>(
            new InvalidOperationException("DMC login expired or was revoked - run `dmc-mcp login` again. (invalid_grant)"));
    }
}
