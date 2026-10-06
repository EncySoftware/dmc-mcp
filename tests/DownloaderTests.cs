using System.Net;
using System.Net.Http.Headers;
using DmcMcp;
using Xunit;

/**
 * The hosted server downloads a component from a link the agent gives it. It must never be talked into fetching
 * from the server's own network — the DMC backend on 127.0.0.1, the database, the Tailscale 100.x hosts.
 */
public class DownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-dl-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("178.104.57.61", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2a00:1450:4001:80b::200e", true)]
    public void OnlyPublicAddressesPass(string ip, bool expected) =>
        Assert.Equal(expected, AddressGuard.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task PlainHttpIsRefused()
    {
        var (path, error) = await new Downloader().Fetch("http://example.com/post.sppx", _dir);
        Assert.Null(path);
        Assert.StartsWith("ERROR: only https://", error);
    }

    /** No network needed: the connect callback refuses before any packet leaves. */
    [Theory]
    [InlineData("https://127.0.0.1/post.sppx")]
    [InlineData("https://10.0.0.5:8443/post.sppx")]
    [InlineData("https://[::1]/post.sppx")]
    public async Task PrivateAddressesAreRefusedAtConnect(string url)
    {
        var (path, error) = await new Downloader().Fetch(url, _dir);
        Assert.Null(path);
        Assert.Contains("not a public address", error);
    }

    [Fact]
    public async Task SavesUnderTheNameFromContentDisposition()
    {
        var handler = new Stub(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"Fanuc 0i.sppx\"" };
            return r;
        });
        var (path, error) = await new Downloader(handler).Fetch("https://files.example.com/d/abc?x=1", _dir);
        Assert.Null(error);
        Assert.Equal("Fanuc 0i.sppx", Path.GetFileName(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path!));
    }

    [Fact]
    public async Task TooLargeIsRefusedAndNothingIsLeft()
    {
        var handler = new Stub(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) });
        var (path, error) = await new Downloader(handler) { MaxBytes = 10 }.Fetch("https://files.example.com/post.sppx", _dir);
        Assert.Null(path);
        Assert.Contains("larger than", error);
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories) : Array.Empty<string>());
    }

    [Fact]
    public async Task AnErrorStatusIsReported()
    {
        var (path, error) = await new Downloader(new Stub(() => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .Fetch("https://files.example.com/post.sppx", _dir);
        Assert.Null(path);
        Assert.Contains("404", error);
    }

    [Theory]
    [InlineData("https://h/a/b/post.sppx", null, "post.sppx")]
    [InlineData("https://h/a/Fanuc%200i.sppx", null, "Fanuc 0i.sppx")]
    [InlineData("https://h/", null, "download")]
    [InlineData("https://h/x", "..\\..\\evil.sppx", "evil.sppx")]
    [InlineData("https://h/x", "../../etc/passwd", "passwd")]
    public void FileNameNeverLeavesTheFolder(string url, string? dispositionName, string expected)
    {
        var cd = dispositionName == null ? null : new ContentDispositionHeaderValue("attachment") { FileName = dispositionName };
        Assert.Equal(expected, Downloader.FileNameFrom(new Uri(url), cd));
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = respond();
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }
}
