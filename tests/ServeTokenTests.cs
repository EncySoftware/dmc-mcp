using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DmcMcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/**
 * The hosted server with per-user sign-in, end to end on 127.0.0.1: the key still lets the server's own account in,
 * a person's access token lets that person in, and every DMC call of a request runs as whoever it let in.
 */
public class ServeTokenTests : IAsyncLifetime
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PublicUrl = "https://dmc.test";
    private const string MetadataUrl = PublicUrl + "/.well-known/oauth-protected-resource/mcp";
    private const string ServerToken = "server-account-token";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "dmc-tok-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TestIssuer _realm = new();
    private readonly CapturedLogs _logs = new();
    private readonly List<WebApplication> _apps = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        try { Directory.Delete(_data, true); } catch { }
    }

    /** Hermes's client by default — tokens are never taken without an allow-list; the test realm's tokens name it. */
    private static TokenSettings Realm(string[]? clients = null, string[]? audiences = null) =>
        new(TestIssuer.Issuer, clients ?? (audiences == null ? new[] { "hermes" } : Array.Empty<string>()),
            audiences ?? Array.Empty<string>());

    private Task<HttpClient> Start(FakeDmcClient dmc, TokenSettings? tokens = null, bool tokensOff = false,
        Downloader? links = null, Func<string, UploadStore>? uploads = null) =>
        Start(dmc, new ServeSettings(Key, Path.Combine(_data, _apps.Count.ToString()), tokensOff ? null : tokens ?? Realm(), PublicUrl),
            links, uploads);

    /** links: the downloader https:// arguments go through (a fake file server); uploads: the store, made for the data folder. */
    private async Task<HttpClient> Start(FakeDmcClient dmc, ServeSettings settings, Downloader? links = null,
        Func<string, UploadStore>? uploads = null)
    {
        var app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, settings, s =>
        {
            s.AddSingleton<IDmcClient>(dmc);
            s.AddSingleton<DmcTokenProvider>(new FakeTokens(ServerToken));
            s.AddSingleton(new IssuerKeys(TestIssuer.Issuer, _realm));
            s.AddSingleton<ILoggerProvider>(_logs);
            s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
            if (links != null) s.AddSingleton(links);
            if (uploads != null) s.AddSingleton(uploads(Path.Combine(settings.DataDir, "uploads")));
        });
        _apps.Add(app);
        _lastData = settings.DataDir;
        await app.StartAsync();
        return new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private string _lastData = "";

    /** What the last server started keeps in its upload store. */
    private string[] Stored()
    {
        var dir = Path.Combine(_lastData, "uploads");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : Array.Empty<string>();
    }

    private static HttpRequestMessage Rpc(string body, string? bearer, string path = "/mcp")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static HttpRequestMessage Initialize(string? bearer, string path = "/mcp") => Rpc(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        bearer, path);

    private static HttpRequestMessage Call(string tool, object arguments, string? bearer, string path = "/mcp") => Rpc(
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = tool, arguments } }),
        bearer, path);

    private static HttpRequestMessage Upload(string? bearer, string path = "/mcp/upload", string name = "Fanuc.sppx")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", name } };
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static async Task<string> UploadRef(HttpClient http, string? bearer, string path = "/mcp/upload")
    {
        var resp = await http.SendAsync(Upload(bearer, path));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return System.Text.RegularExpressions.Regex.Match(await resp.Content.ReadAsStringAsync(), "upload:[0-9a-f]{32}").Value;
    }

    private static FakeDmcClient Publishing()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Done(new ImportComponent("Fanuc", "POST_PROCESSOR", "p1", AiEnriched: false)));
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc");
        return dmc;
    }

    private static string Challenge(HttpResponseMessage resp) => string.Join(", ", resp.Headers.WwwAuthenticate.Select(h => h.ToString()));

    private const string Link = "https://files.example.com/post.sppx";

    /** A file server for links: answers three bytes and counts the downloads asked of it. */
    private sealed class Files : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
                RequestMessage = request,
            });
        }
    }

    // ------------------------------------------------------------------ the key, as in 0.8.0

    [Fact]
    public async Task TheKeyByHeaderActsAsTheServersAccount()
    {
        var dmc = new FakeDmcClient();
        var http = await Start(dmc);
        var resp = await http.SendAsync(Call("list_my_posts", new { }, Key));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { ServerToken }, dmc.TokensOf("MyProducts"));
    }

    [Fact]
    public async Task TheKeyInThePathActsAsTheServersAccount()
    {
        var dmc = new FakeDmcClient();
        var http = await Start(dmc);
        var resp = await http.SendAsync(Call("list_my_posts", new { }, null, "/mcp/" + Key));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { ServerToken }, dmc.TokensOf("MyProducts"));
    }

    /** As in 0.8.0: a client configured with the key in its URL may send some other header — the key still lets it in. */
    [Fact]
    public async Task TheKeyInThePathIgnoresAHeaderThatIsNoToken()
    {
        var http = await Start(new FakeDmcClient());
        var resp = await http.SendAsync(Initialize("not-a-token", "/mcp/" + Key));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ------------------------------------------------------------------ a person's token

    [Fact]
    public async Task APersonsTokenActsAsThePerson()
    {
        var dmc = new FakeDmcClient();
        var http = await Start(dmc);
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var resp = await http.SendAsync(Call("list_my_posts", new { }, anna));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { anna }, dmc.TokensOf("MyProducts"));
    }

    /** A token decides who the call runs as even when the key is in the path — the shared account never stands in for a person. */
    [Fact]
    public async Task ATokenBesideTheKeyInThePathActsAsThePerson()
    {
        var dmc = new FakeDmcClient();
        var http = await Start(dmc);
        var anna = _realm.Token();
        var resp = await http.SendAsync(Call("list_my_posts", new { }, anna, "/mcp/" + Key));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { anna }, dmc.TokensOf("MyProducts"));
    }

    [Fact]
    public async Task AnExpiredTokenBesideTheKeyInThePathIsStillRefused()
    {
        var http = await Start(new FakeDmcClient());
        var expired = _realm.Token(edit: c => c["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600);
        var resp = await http.SendAsync(Initialize(expired, "/mcp/" + Key));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    /**
     * Two people through Hermes at the same moment: each call reaches DMC with its own person's token. The fake DMC
     * holds both calls until both have arrived, so they really overlap.
     */
    [Fact]
    public async Task TwoPeopleAtOnceEachReachDmcWithTheirOwnToken()
    {
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var boris = _realm.Token(sub: "sub-boris", name: "boris@example.com");
        var dmc = new TwoAtOnce(new Dictionary<string, string> { [anna] = "anna", [boris] = "boris" });
        var http = await Start(dmc);

        var a = http.SendAsync(Call("list_my_posts", new { }, anna));
        var b = http.SendAsync(Call("list_my_posts", new { }, boris));
        var bodies = await Task.WhenAll(
            a.ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap(),
            b.ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap());

        Assert.Contains("Post of anna", bodies[0]);
        Assert.DoesNotContain("Post of boris", bodies[0]);
        Assert.Contains("Post of boris", bodies[1]);
        Assert.DoesNotContain("Post of anna", bodies[1]);
        Assert.Equal(new[] { anna, boris }.OrderBy(x => x), dmc.Seen.OrderBy(x => x));
    }

    /** list_my_posts answers from the token it was given; both calls wait for each other inside DMC. */
    private sealed class TwoAtOnce(Dictionary<string, string> names) : FakeDmcClient, IDmcClient
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public ConcurrentBag<string> Seen { get; } = new();

        async Task<IReadOnlyList<ProductInfo>> IDmcClient.MyProducts(string accessToken)
        {
            Seen.Add(accessToken);
            if (Interlocked.Increment(ref _arrived) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var who = names[accessToken];
            return new[] { Post("p-" + who, "Post of " + who) };
        }
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task WithoutCredentialsThe401NamesTheMetadata()
    {
        var http = await Start(new FakeDmcClient());
        var resp = await http.SendAsync(Initialize(null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{MetadataUrl}\"", Challenge(resp));
    }

    [Fact]
    public async Task AnUploadWithoutCredentialsNamesTheMetadataToo()
    {
        var http = await Start(new FakeDmcClient());
        var resp = await http.SendAsync(Upload(null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains($"resource_metadata=\"{MetadataUrl}\"", Challenge(resp));
    }

    /** Every way a token can be wrong ends at the door, with invalid_token and a reason — never the token itself. */
    [Theory]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("other-issuer")]
    [InlineData("alg-none")]
    [InlineData("hs256")]
    [InlineData("id-token")]
    [InlineData("garbage")]
    public async Task ABadTokenIs401InvalidToken(string kind)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = kind switch
        {
            "expired" => _realm.Token(edit: c => c["exp"] = now - 600),
            "not-yet-valid" => _realm.Token(edit: c => { c["nbf"] = now + 600; c["exp"] = now + 1200; }),
            "other-issuer" => _realm.Token(edit: c => c["iss"] = "https://evil.test/realms/licsys"),
            "alg-none" => _realm.Unsigned(),
            "hs256" => _realm.Hs256(Encoding.UTF8.GetBytes("a secret a forger picked for itself")),
            "id-token" => _realm.Token(edit: c => c["typ"] = "ID"),
            _ => "eyJhbGciOiJSUzI1NiJ9.bm90IGpzb24.c2ln",
        };
        var http = await Start(new FakeDmcClient());
        var resp = await http.SendAsync(Initialize(token));
        var body = await resp.Content.ReadAsStringAsync();
        var challenge = Challenge(resp);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.StartsWith("Bearer error=\"invalid_token\", error_description=\"", challenge);
        Assert.EndsWith($"resource_metadata=\"{MetadataUrl}\"", challenge);
        Assert.True(challenge.All(ch => ch is >= ' ' and <= '~'), "a header value is plain ASCII: " + challenge);
        Assert.Contains("invalid_token", body);
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain(token, challenge);
    }

    [Fact]
    public async Task AClientOffTheListIsRefusedAtTheDoor()
    {
        var http = await Start(new FakeDmcClient(), Realm(clients: new[] { "hermes-prod" }));
        var resp = await http.SendAsync(Initialize(_realm.Token(edit: c => c["azp"] = "dealer-space")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("invalid_token", Challenge(resp));
    }

    [Fact]
    public async Task AnAudienceOffTheListIsRefusedAtTheDoor()
    {
        var http = await Start(new FakeDmcClient(), Realm(audiences: new[] { "dmc-mcp" }));
        var refused = await http.SendAsync(Initialize(_realm.Token(edit: c => c["aud"] = "account")));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var welcome = await http.SendAsync(Initialize(_realm.Token(edit: c => c["aud"] = new[] { "account", "dmc-mcp" })));
        Assert.Equal(HttpStatusCode.OK, welcome.StatusCode);
    }

    [Fact]
    public async Task AWrongKeyIs401()
    {
        var http = await Start(new FakeDmcClient());
        var resp = await http.SendAsync(Initialize("nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("invalid_token", Challenge(resp));
    }

    /** Keycloak down before the server ever saw its keys: the token is not to blame, so not a 401 that sends the client to sign in again. */
    [Fact]
    public async Task NoKeysToCheckWithIs503()
    {
        _realm.Down = true;
        var http = await Start(new FakeDmcClient());
        var resp = await http.SendAsync(Initialize(_realm.Token()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ------------------------------------------------------------------ uploads by owner

    [Fact]
    public async Task AnUploadIsItsSendersOnly()
    {
        var dmc = Publishing();
        var http = await Start(dmc);
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var boris = _realm.Token(sub: "sub-boris", name: "boris@example.com");
        var annas = await UploadRef(http, anna);

        foreach (var other in new[] { boris, Key })
        {
            var body = await (await http.SendAsync(Call("publish_post", new { file = annas, force = true }, other))).Content.ReadAsStringAsync();
            Assert.Contains("is unknown or expired", body); // the same answer as for an id that never existed
        }
        Assert.Empty(dmc.Starts);

        var mine = await (await http.SendAsync(Call("publish_post", new { file = annas, force = true }, anna))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("unknown or expired", mine);
        Assert.Single(dmc.Starts);
        Assert.Equal(new[] { anna }, dmc.TokensOf("StartImport"));
    }

    [Fact]
    public async Task TheKeysUploadsAreNotAPersons()
    {
        var dmc = Publishing();
        var http = await Start(dmc);
        var keys = await UploadRef(http, null, "/mcp/" + Key + "/upload");
        var body = await (await http.SendAsync(Call("publish_post", new { file = keys, force = true }, _realm.Token()))).Content.ReadAsStringAsync();
        Assert.Contains("is unknown or expired", body);
        Assert.Empty(dmc.Starts);

        await http.SendAsync(Call("publish_post", new { file = keys, force = true }, Key));
        Assert.Equal(new[] { ServerToken }, dmc.TokensOf("StartImport"));
    }

    // ------------------------------------------------------------------ the server's own disk

    private static MeInfo Customer(string name) => new(name, new[] { "USER" }, "acc-" + name, null);

    /** A licsys account alone buys no room on this server: someone DMC does not let publish has nothing stored. */
    [Fact]
    public async Task ANonPublishersUploadIsRefused()
    {
        var dmc = new FakeDmcClient { Who = Customer("anna@example.com") };
        var http = await Start(dmc);
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var resp = await http.SendAsync(Upload(anna));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("anna@example.com", body);
        Assert.Contains("not a Publisher", body);
        Assert.Empty(Stored());
        Assert.Contains(anna, dmc.TokensOf("Me"));
    }

    /** DMC does not say who someone is: nothing is stored on a guess, and the answer says to try again. */
    [Fact]
    public async Task AnUploadWhoseSenderDmcCannotConfirmIs503()
    {
        var http = await Start(new FakeDmcClient { FailMe = new DmcHttpException(502, "") });
        var resp = await http.SendAsync(Upload(_realm.Token()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.NotNull(resp.Headers.RetryAfter);
        Assert.Empty(Stored());
    }

    /** inspect_archive asks DMC nothing itself — the gate is what stands between someone's link and this disk. */
    [Fact]
    public async Task ANonPublishersLinkIsNotFetched()
    {
        var files = new Files();
        var http = await Start(new FakeDmcClient { Who = Customer("anna@example.com") }, links: new Downloader(files));
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var body = await (await http.SendAsync(Call("inspect_archive", new { file = Link }, anna))).Content.ReadAsStringAsync();
        Assert.Contains("not a Publisher", body);
        Assert.Equal(0, files.Calls);
    }

    [Fact]
    public async Task APublishersLinkIsFetched()
    {
        var files = new Files();
        var http = await Start(Publishing(), links: new Downloader(files));
        var body = await (await http.SendAsync(Call("inspect_archive", new { file = Link }, _realm.Token()))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("not a Publisher", body);
        Assert.Equal(1, files.Calls);
    }

    /** The key is the operator's own: DMC is not asked who it is before its files, as in 0.8.0. */
    [Fact]
    public async Task TheKeysFilesNeedNoRoleCheck()
    {
        var files = new Files();
        var dmc = new FakeDmcClient { Who = Customer("hermes") };
        var http = await Start(dmc, links: new Downloader(files));
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload(Key))).StatusCode);
        await http.SendAsync(Call("inspect_archive", new { file = Link }, Key));
        Assert.Equal(1, files.Calls);
        Assert.All(dmc.TokensOf("Me"), t => Assert.Equal(ServerToken, t)); // only the sign-in's own check at startup
    }

    /** Two upload slots in all and one per person: one person cannot hold both while everyone else waits. */
    [Fact]
    public async Task OnePersonHoldsOneUploadSlotAtMost()
    {
        var http = await Start(Publishing());
        var annas = Caller.Person("any", "anna", TestIssuer.Issuer, "sub-anna").Owner;
        using var running = _apps[^1].Services.GetRequiredService<UploadStore>().TryBegin(annas, out _);
        Assert.NotNull(running);
        var second = await http.SendAsync(Upload(_realm.Token(sub: "sub-anna")));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("you already have an upload running", await second.Content.ReadAsStringAsync());
        var boris = await http.SendAsync(Upload(_realm.Token(sub: "sub-boris", name: "boris@example.com")));
        Assert.Equal(HttpStatusCode.OK, boris.StatusCode);
    }

    /** Each person's uploads have a share of the store: one person cannot fill it for everyone for a day. */
    [Fact]
    public async Task OnePersonCannotFillTheStore()
    {
        var http = await Start(Publishing(), uploads: dir => new UploadStore(dir) { MaxBytesPerPerson = 4 });
        var anna = _realm.Token(sub: "sub-anna");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload(anna))).StatusCode); // 3 bytes of anna's 4
        var over = await http.SendAsync(Upload(anna));
        Assert.Equal((HttpStatusCode)507, over.StatusCode);
        Assert.Contains("your share", await over.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload(_realm.Token(sub: "sub-boris", name: "boris@example.com")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload(Key))).StatusCode);
    }

    // ------------------------------------------------------------------ discovery

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task TheResourceMetadataIsPublic(string path)
    {
        var http = await Start(new FakeDmcClient());
        var resp = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(new[] { "authorization_servers", "bearer_methods_supported", "resource", "scopes_supported" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(PublicUrl + "/mcp", root.GetProperty("resource").GetString());
        Assert.Equal(new[] { TestIssuer.Issuer }, root.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "header" }, root.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "openid" }, root.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    /** DMC_MCP_ISSUER=off: the key alone, exactly as in 0.8.0 — no metadata, a bare challenge, a token is just a wrong key. */
    [Fact]
    public async Task WithTokensOffItIsTheKeyAlone()
    {
        var http = await Start(new FakeDmcClient(), tokensOff: true);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode);
        var none = await http.SendAsync(Initialize(null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("Bearer", Challenge(none));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize(_realm.Token()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Initialize(Key))).StatusCode);
        Assert.Equal(0, _realm.JwksFetches);
    }

    [Fact]
    public void TheStartupLineSaysWhichWaysInAreOpen()
    {
        var open = ServeCommand.Modes(new ServeSettings(Key, _data, Realm(), PublicUrl));
        Assert.Contains("key", open);
        Assert.Contains("tokens from " + TestIssuer.Issuer, open);
        Assert.Contains("clients hermes", open);
        Assert.Contains("audience not checked", open);
        Assert.DoesNotContain(Key, open);

        var narrowed = ServeCommand.Modes(new ServeSettings(Key, _data, Realm(new[] { "hermes", "hermes-dev" }, new[] { "dmc-mcp" }), PublicUrl));
        Assert.Contains("clients hermes, hermes-dev", narrowed);
        Assert.Contains("audience dmc-mcp", narrowed);

        Assert.Contains("clients any, audience dmc-mcp",
            ServeCommand.Modes(new ServeSettings(Key, _data, Realm(audiences: new[] { "dmc-mcp" }), PublicUrl)));
        Assert.Contains("tokens off", ServeCommand.Modes(new ServeSettings(Key, _data, null, PublicUrl)));
    }

    // ------------------------------------------------------------------ the environment

    private static Func<string, string?> Env(params (string Name, string? Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    /**
     * Upgrading from 0.8.0 with its dmc-mcp.env — the key alone — must not open the door to every licsys account:
     * tokens stay off, the startup line says what turns them on, and a token at the door is just a wrong key.
     */
    [Fact]
    public async Task AnEnvFileFrom080KeepsTheKeyAlone()
    {
        var (settings, error) = ServeCommand.Read(Env(("DMC_MCP_KEY", Key), ("DMC_MCP_DATA", Path.Combine(_data, "env"))));
        Assert.Null(error);
        Assert.Null(settings!.Tokens);
        var line = ServeCommand.Modes(settings);
        Assert.Contains("tokens off", line);
        Assert.Contains("DMC_MCP_CLIENTS", line);

        var http = await Start(new FakeDmcClient(), settings);
        var resp = await http.SendAsync(Initialize(_realm.Token()));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("Bearer", Challenge(resp));
        Assert.Equal(0, _realm.Attempts);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Initialize(Key))).StatusCode);
    }

    [Fact]
    public void NamingTheAgentsClientOrAnAudienceTurnsTokensOn()
    {
        var (settings, error) = ServeCommand.Read(Env(("DMC_MCP_KEY", Key), ("DMC_MCP_CLIENTS", "hermes")));
        Assert.Null(error);
        Assert.Equal(TokenSettings.DefaultIssuer, settings!.Tokens!.Issuer);
        Assert.Equal(new[] { "hermes" }, settings.Tokens.Clients);
        Assert.Equal(Brand.Site, settings.PublicUrl);
        Assert.Equal("/data", settings.DataDir);
        Assert.Contains("clients hermes", ServeCommand.Modes(settings));

        var (byAudience, _) = ServeCommand.Read(Env(("DMC_MCP_KEY", Key), ("DMC_MCP_AUDIENCE", "dmc-mcp")));
        Assert.Equal(new[] { "dmc-mcp" }, byAudience!.Tokens!.Audiences);

        var (off, _) = ServeCommand.Read(Env(("DMC_MCP_KEY", Key), ("DMC_MCP_ISSUER", "off"), ("DMC_MCP_CLIENTS", "hermes")));
        Assert.Null(off!.Tokens);
        Assert.Contains("DMC_MCP_ISSUER=off", ServeCommand.Modes(off));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("too-short", null, null)]
    [InlineData(Key, "http://kc.example/realms/licsys", null)]
    [InlineData(Key, null, "http://dmc.example")]
    [InlineData(Key, null, "https://dmc.example/sub")]
    public void WhatCannotStartIsSaid(string? key, string? issuer, string? publicUrl)
    {
        var (settings, error) = ServeCommand.Read(Env(("DMC_MCP_KEY", key), ("DMC_MCP_ISSUER", issuer),
            ("DMC_MCP_PUBLIC_URL", publicUrl), ("DMC_MCP_CLIENTS", "hermes")));
        Assert.Null(settings);
        Assert.NotNull(error);
        Assert.DoesNotContain(Key, error);
    }

    // ------------------------------------------------------------------ nothing logs a token

    /**
     * A whole session's worth of traffic with every log category at Trace (ASP.NET's own stays at Warning, as in
     * production): accepted, refused and garbled tokens, an upload, a tool that DMC refuses — no line holds a token,
     * nor its signature on its own.
     */
    [Fact]
    public async Task NoTokenEverReachesTheLog()
    {
        var dmc = Publishing();
        dmc.FailStatus = new DmcHttpException(403, """{"error":"Publisher role required"}""");
        dmc.Who = new MeInfo("anna@example.com", new[] { "USER" }, "acc-anna", null);
        var boris = _realm.Token(sub: "sub-boris", name: "boris@example.com");
        dmc.People[boris] = new MeInfo("boris@example.com", new[] { "USER", "DEALER" }, "acc-boris", null);
        var http = await Start(dmc, links: new Downloader(new Files()));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var anna = _realm.Token(sub: "sub-anna");
        var tokens = new[]
        {
            anna,
            boris,
            _realm.Token(edit: c => c["exp"] = now - 600),
            _realm.Token(edit: c => c["typ"] = "ID"),
            _realm.Unsigned(),
            "eyJhbGciOiJSUzI1NiJ9.bm90IGpzb24.c2lnbmF0dXJlLWdhcmJhZ2U",
        };
        foreach (var t in tokens) await http.SendAsync(Initialize(t));
        await http.SendAsync(Call("list_my_posts", new { }, anna));
        await http.SendAsync(Call("submit_post", new { id = "p1" }, anna)); // DMC answers 403, the error names the account
        await http.SendAsync(Upload(anna));                                   // anna is no Publisher: refused at the disk
        await http.SendAsync(Call("inspect_archive", new { file = Link }, anna));
        var reference = await UploadRef(http, boris);
        await http.SendAsync(Call("publish_post", new { file = reference, force = true }, boris));
        await http.SendAsync(Call("inspect_archive", new { file = Link }, boris));
        await http.GetAsync("/.well-known/oauth-protected-resource/mcp");

        // The capture does see the door and the tools: a refusal is logged — by its reason.
        Assert.Contains(_logs.Lines, l => l.Contains("Refused a token: the token has expired"));
        Assert.Contains(_logs.Lines, l => l.Contains("Ways in: the key"));
        if (Environment.GetEnvironmentVariable("DMC_TEST_DUMP_LOGS") is { Length: > 0 } dump) File.WriteAllLines(dump, _logs.Lines);
        foreach (var t in tokens)
            foreach (var line in _logs.Lines)
            {
                Assert.DoesNotContain(t, line);
                if (t.Split('.')[^1] is { Length: > 0 } signature) Assert.DoesNotContain(signature, line);
            }
    }

    /** Every log line, formatted, with its exception — what an operator's `docker logs` would show. */
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string category) => new Logger(category, Lines);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) =>
                lines.Enqueue($"{level} {category}: {format(state, e)} {e}");
        }
    }
}
