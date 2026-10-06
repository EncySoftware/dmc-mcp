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
