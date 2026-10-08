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
        await As(Caller.Server, () => Tools(client).PublishPost(saved.Ref, force: true));
        var start = Assert.Single(client.Starts);
        Assert.Equal("Fanuc.sppx", Path.GetFileName(start.File));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_dir, "uploads")), Path.GetFullPath(start.File));
    }

    [Fact]
    public async Task NotSignedInNamesTheContainerCommand()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        var answer = await As(Caller.Server, () => Tools(new FakeDmcClient(), token: null).PublishPost(saved.Ref));
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, answer);
    }

    /** A lost offline session (idle too long, revoked) points at the container command too, not at `dmc-mcp login`. */
    [Fact]
    public async Task ExpiredSignInNamesTheContainerCommand()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        var answer = await As(Caller.Server, () => Tools(new FakeDmcClient(), new ExpiredTokens()).PublishPost(saved.Ref));
        Assert.StartsWith("ERROR", answer);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, answer);
    }

    /** The client gave up (cancelled, or its connection went): the download behind the call stops with it. */
    [Fact(Timeout = 15000)]
    public async Task ACancelledCallStopsItsDownload()
    {
        var tmp = Path.Combine(_dir, "tmp");
        var tools = new DmcTools(new FakeDmcClient(), new FakeTokens("tok"),
            new FileInputs(true, Uploads(), new Downloader(new Silent()), tmp));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tools.PublishPost("https://files.example.com/post.sppx", force: true, cancellationToken: cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(tmp));
    }

    [Fact(Timeout = 15000)]
    public async Task ACancelledFolderCallStopsItsDownload()
    {
        var tmp = Path.Combine(_dir, "tmp");
        var tools = new DmcTools(new FakeDmcClient(), new FakeTokens("tok"),
            new FileInputs(true, Uploads(), new Downloader(new Silent()), tmp));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tools.PublishFolder("https://files.example.com/posts.zip", dryRun: true, cancellationToken: cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(tmp));
    }

    // ------------------------------------------------------------- a person's own token (0.9.0)

    private static readonly Caller Anna = Caller.Person("tok-anna", "anna@example.com", TestIssuer.Issuer, "sub-anna");
    private static readonly Caller Boris = Caller.Person("tok-boris", "boris@example.com", TestIssuer.Issuer, "sub-boris");

    private static async Task<string> As(Caller caller, Func<Task<string>> call)
    {
        using (Caller.Enter(caller)) return await call();
    }

    [Fact]
    public async Task APersonsCallCarriesTheirTokenNotTheServers()
    {
        var client = new FakeDmcClient();
        await As(Anna, () => Tools(client, "server-token").ListMyPosts());
        Assert.Equal(new[] { "tok-anna" }, client.TokensOf("MyProducts"));
    }

    /** The server's own sign-in is the key's business: a person with a token of their own does not need it. */
    [Fact]
    public async Task APersonNeedsNoServerSignIn()
    {
        var client = new FakeDmcClient();
        var answer = await As(Anna, () => Tools(client, token: null).ListMyPosts());
        Assert.DoesNotContain("ERROR", answer);
        Assert.Equal(new[] { "tok-anna" }, client.TokensOf("MyProducts"));
    }

    [Fact]
    public async Task APersonsUploadIsTheirs()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "Fanuc.sppx", Anna.Owner);
        var client = new FakeDmcClient();
        client.Progress.Enqueue(FakeDmcClient.Done(new ImportComponent("Fanuc", "POST_PROCESSOR", "p1", AiEnriched: false)));
        await As(Anna, () => Tools(client).PublishPost(saved.Ref, force: true));
        Assert.Single(client.Starts);
    }

    /** Someone else's upload is answered exactly like one that never existed: nothing tells the two apart. */
    [Fact]
    public async Task AnotherPersonsUploadIsUnknown()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "Fanuc.sppx", Anna.Owner);
        var client = new FakeDmcClient();
        var theirs = await As(Boris, () => Tools(client).PublishPost(saved.Ref, force: true));
        var nobodys = await As(Boris, () => Tools(client).PublishPost("upload:0123456789abcdef0123456789abcdef", force: true));
        Assert.Equal(nobodys.Replace("0123456789abcdef0123456789abcdef", saved.Id), theirs);
        Assert.Empty(client.Starts);
    }

    [Fact]
    public async Task A403NamesThePersonWhoIsNotAPublisher()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx", Anna.Owner);
        var client = new FakeDmcClient
        {
            FailStart = new DmcHttpException(403, """{"error":"Publisher role required"}"""),
            Who = new MeInfo("anna@example.com", new[] { "USER" }, "acc-anna", null),
        };
        var answer = await As(Anna, () => Tools(client).PublishPost(saved.Ref, force: true));
        Assert.StartsWith("ERROR", answer);
        Assert.Contains("anna@example.com", answer);
        Assert.Contains("not a Publisher", answer);
        Assert.Equal(new[] { "tok-anna" }, client.TokensOf("Me"));
    }

    /** A publisher refused for another reason — someone else's card, an admin-only step — hears DMC's own reason. */
    [Fact]
    public async Task A403ForAPublisherQuotesDmc()
    {
        var client = new FakeDmcClient
        {
            FailStatus = new DmcHttpException(403, """{"error":"Not the product owner"}"""),
            Who = new MeInfo("anna@example.com", new[] { "USER", "DEALER" }, "acc-anna", null),
        };
        client.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc");
        var answer = await As(Anna, () => Tools(client).SubmitPost("p1"));
        Assert.StartsWith("ERROR", answer);
        Assert.Contains("anna@example.com", answer);
        Assert.Contains("Not the product owner", answer);
        Assert.DoesNotContain("not a Publisher", answer);
    }

    /** DMC no longer takes the person's token: say so, by name — not "run dmc-mcp login", which is nobody's to run. */
    [Fact]
    public async Task A401UnderAPersonsTokenSaysTheTokenWasNotAccepted()
    {
        var client = new FakeDmcClient
        {
            FailStatus = new DmcHttpException(401, ""),
            FailMe = new DmcHttpException(401, ""),
        };
        client.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc");
        var answer = await As(Anna, () => Tools(client).SubmitPost("p1"));
        Assert.StartsWith("ERROR", answer);
        Assert.Contains("anna@example.com", answer);
        Assert.Contains("token", answer);
        Assert.DoesNotContain("dmc-mcp login", answer);
        Assert.DoesNotContain(SignInKeepAlive.HostedLoginCommand, answer);
    }

    /** /auth/me is asked once for a run of errors under the same token, not once per error. */
    [Fact]
    public async Task WhoThePersonIsIsAskedOncePerToken()
    {
        var client = new FakeDmcClient
        {
            FailStatus = new DmcHttpException(403, """{"error":"Publisher role required"}"""),
            Who = new MeInfo("anna@example.com", new[] { "USER" }, "acc-anna", null),
        };
        client.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc");
        client.Products["p2"] = FakeDmcClient.Post("p2", "Siemens");
        var tools = Tools(client);
        await As(Anna, () => tools.SubmitPost("p1"));
        await As(Anna, () => tools.SubmitPost("p2"));
        Assert.Equal(1, client.MeCalls);
    }

    /**
     * On the hosted server every request has a caller — the door sets one. Work still running after its request has
     * ended (the client went away) has none, and must not go on as the server's own account: a person's call would
     * then be acting with the shared account's rights.
     */
    [Fact]
    public async Task AfterItsRequestEndsAPersonsCallNeverGoesOnAsTheServer()
    {
        var client = new FakeDmcClient();
        var tools = Tools(client, "server-token");
        var gate = new TaskCompletionSource();
        Task<string> lingering;
        using (Caller.Enter(Anna))
            lingering = Task.Run(async () => { await gate.Task; return await tools.ListMyPosts(); });
        gate.SetResult();
        var answer = await lingering;
        Assert.StartsWith("ERROR", answer);
        Assert.Empty(client.TokensOf("MyProducts"));
    }

    /**
     * A person's token lives minutes; an import may be waited on for ten. When DMC stops taking the token mid-wait,
     * the import is not lost: it carries on, and check_import shows it once the client has a fresh token.
     */
    [Fact]
    public async Task AWaitThatOutlivesTheTokenSaysTheImportGoesOn()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx", Anna.Owner);
        var client = new FakeDmcClient { FailProgress = new DmcHttpException(401, "") };
        var answer = await As(Anna, () => Tools(client).PublishPost(saved.Ref, force: true));
        Assert.Contains("check_import", answer);
        Assert.Contains("still running", answer);
        Assert.Contains(client.Starts.Single().ImportId, answer);
    }

    /** A server that accepts the connection and never answers. */
    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ExpiredTokens : DmcTokenProvider
    {
        public override Task<string?> GetAccessToken() => Task.FromException<string?>(
            new InvalidOperationException("DMC login expired or was revoked - run `dmc-mcp login` again. (invalid_grant)"));
    }
}
