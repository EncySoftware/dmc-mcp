using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DmcMcp;
using Xunit;

/** The hosted server end to end on 127.0.0.1: the key at the door, MCP behind it, uploads beside it. */
public class ServeTests : IAsyncLifetime
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "dmc-serve-" + Guid.NewGuid().ToString("N")[..8]);
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, Key, _data);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_data, true); } catch { }
    }

    private static HttpRequestMessage Initialize(string path, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static HttpRequestMessage Rpc(string path, string body, string? session)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        if (session != null) req.Headers.Add("Mcp-Session-Id", session);
        return req;
    }

    /** initialize, then one more request in the same session (if the server keeps sessions). */
    private async Task<HttpResponseMessage> AfterInitialize(string body)
    {
        var init = await _http.SendAsync(Initialize("/mcp", Key));
        init.EnsureSuccessStatusCode();
        var session = init.Headers.TryGetValues("Mcp-Session-Id", out var v) ? v.First() : null;
        await _http.SendAsync(Rpc("/mcp", """{"jsonrpc":"2.0","method":"notifications/initialized"}""", session));
        return await _http.SendAsync(Rpc("/mcp", body, session));
    }

    /** The request's CancellationToken is bound by the SDK — an agent never sees it as a tool argument. */
    [Fact]
    public async Task ToolSchemasDoNotShowTheCancellationToken()
    {
        var resp = await AfterInitialize("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("publish_post", body);
        Assert.DoesNotContain("cancellationToken", body, StringComparison.OrdinalIgnoreCase);
    }

    /** The SDK reads a JSON-RPC body whole into memory: only uploads may be large, /mcp keeps Kestrel's 30 MB. */
    [Fact]
    public async Task AHugeJsonRpcBodyIsRefused()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"x\":\"" + new string('a', 31_000_000) + "\"}}";
        var resp = await _http.SendAsync(Rpc("/mcp", json, null));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    [Fact]
    public async Task AnUploadMayBeLargerThanAJsonRpcBody()
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[31_000_000]), "file", "kit.zip" } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/upload") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"size\":31000000", await resp.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Upload(byte[] bytes, string name)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/upload") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return req;
    }

    [Fact]
    public async Task TwoUploadsRunningMakeAThirdWait()
    {
        var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<UploadStore>(_app.Services);
        using var a = store.TryBegin();
        using var b = store.TryBegin();
        var resp = await _http.SendAsync(Upload(new byte[] { 1 }, "a.sppx"));
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }

    [Fact]
    public async Task AFullStoreAnswers507()
    {
        var data = _data + "-full";
        await using var app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, Key, data,
            s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(s,
                new UploadStore(Path.Combine(data, "uploads")) { MaxTotalBytes = 4 }));
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload(new byte[] { 1, 2, 3 }, "a.sppx"))).StatusCode);
            var resp = await http.SendAsync(Upload(new byte[] { 1, 2, 3 }, "b.sppx"));
            Assert.Equal((HttpStatusCode)507, resp.StatusCode);
            Assert.Contains("full", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
            try { Directory.Delete(data, true); } catch { }
        }
    }

    /** A container stopped mid-download or mid-unpack left a folder in tmp; nothing there belongs to a live call. */
    [Fact]
    public async Task TmpIsEmptiedAtStartup()
    {
        var data = _data + "-tmp";
        var leftover = Path.Combine(data, "tmp", "0123", "post.sppx");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "x");
        await using var app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, Key, data);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(data, "tmp")));
        try { Directory.Delete(data, true); } catch { }
    }

    [Fact]
    public async Task WithoutTheKeyIt401s()
    {
        var resp = await _http.SendAsync(Initialize("/mcp", null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task BearerKeyReachesMcp()
    {
        var resp = await _http.SendAsync(Initialize("/mcp", Key));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("serverInfo", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task KeyInThePathReachesMcp()
    {
        var resp = await _http.SendAsync(Initialize("/mcp/" + Key, null));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("upload", await resp.Content.ReadAsStringAsync()); // the instructions tell the agent how
    }

    [Fact]
    public async Task UploadReturnsAReference()
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "Fanuc.sppx" } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/upload") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Matches("\"file\":\"upload:[0-9a-f]{32}\"", body);
        Assert.Contains("\"name\":\"Fanuc.sppx\"", body);
    }

    [Fact]
    public async Task UploadWithoutTheKeyIs401()
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1 }), "file", "a.sppx" } };
        var resp = await _http.PostAsync("/mcp/upload", form);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task UploadWithoutAFileExplainsTheField()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/upload") { Content = new MultipartFormDataContent() };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("field", await resp.Content.ReadAsStringAsync());
    }
}
