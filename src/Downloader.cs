using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace DmcMcp;

/// <summary>
/// Which addresses the hosted server may download from: the public internet only. Loopback, private ranges,
/// link-local (cloud metadata), CGNAT (Tailscale's 100.64/10), multicast, reserved, documentation and NAT64 are
/// refused, and an IPv4-mapped IPv6 address is judged by its IPv4 part.
/// </summary>
public static class AddressGuard
{
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224);
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
            var b = a.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                                        // fc00::/7
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return false; // 64:ff9b::/96 NAT64
            if (b[0] == 0x20 && b[1] == 0x02) return false;                                 // 2002::/16 6to4
            return true;
        }
        return false;
    }
}

/// <summary>A file name that cannot climb out of the folder it is saved in.</summary>
public static class FileNames
{
    /** Linux allows 255 bytes for a name; this leaves room and is far beyond any component's real name. */
    public const int MaxBytes = 200;

    public static string Safe(string? raw)
    {
        var name = (raw ?? "").Trim().Trim('"').Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '<', '>', '|' }))
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "download" : Shortened(name);
    }

    /** At most MaxBytes of UTF-8, the extension kept: the type check after the download reads it. */
    private static string Shortened(string name)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(name) <= MaxBytes) return name;
        var ext = Path.GetExtension(name);
        if (ext.Length > 16) ext = "";
        var stem = name[..^ext.Length];
        while (stem.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(stem + ext) > MaxBytes)
        {
            stem = stem[..^1];
            if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        }
        return stem.Length == 0 ? "download" + ext : stem + ext;
    }
}

/// <summary>
/// Fetches a file the hosted server's caller points at by an https link: at most 5 redirects, 1 GB and 10 minutes
/// for the whole download, body included, and every connection — redirects included — goes only to a public address, checked when the socket opens, so
/// neither a redirect nor a DNS answer can steer it into the server's own network.
/// </summary>
public sealed class Downloader
{
    internal const long OneGigabyte = 1024L * 1024 * 1024;
    private readonly HttpClient _http;

    internal long MaxBytes { get; init; } = OneGigabyte;

    /** The whole download, headers and body: HttpClient.Timeout alone stops at the headers. */
    internal TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(10);

    public Downloader() : this(GuardedHandler()) { }

    // No HttpClient.Timeout: with ResponseHeadersRead it would stop at the headers. Limit covers the body too.
    internal Downloader(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    private static SocketsHttpHandler GuardedHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, ct);
            var target = addresses.FirstOrDefault(AddressGuard.IsPublic)
                ?? throw new HttpRequestException($"{host} is not a public address — the hosted server only downloads from the internet");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<(string? Path, string? Error)> Fetch(string url, string intoDir, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, "ERROR: only https:// links can be downloaded.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        string? path = null;
        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!resp.IsSuccessStatusCode)
                return (null, $"ERROR: {uri.Host} answered {(int)resp.StatusCode} for the link.");
            if (resp.Content.Headers.ContentLength > MaxBytes)
                return (null, $"ERROR: the file behind the link is larger than {MaxBytes / (1024 * 1024)} MB.");
            var name = FileNameFrom(resp.RequestMessage?.RequestUri ?? uri, resp.Content.Headers.ContentDisposition);
            Directory.CreateDirectory(intoDir);
            path = Path.Combine(intoDir, name);
            long total = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(limit.Token))
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, limit.Token)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), limit.Token);
                }
            }
            if (total > MaxBytes)
            {
                File.Delete(path);
                return (null, $"ERROR: the file behind the link is larger than {MaxBytes / (1024 * 1024)} MB.");
            }
            return (path, null);
        }
        catch (Exception e)
        {
            // A cut connection is an HttpIOException (an IOException, not an HttpRequestException), a name the disk
            // refuses another IOException: whatever it was, the half-written file goes.
            if (path != null) try { File.Delete(path); } catch { }
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw; // the caller gave up
            if (e is OperationCanceledException)
                return (null, $"ERROR: the download took longer than {Took(Limit)}.");
            return (null, "ERROR: could not download the link — "
                          + (e is HttpRequestException ? e.InnerException?.Message ?? e.Message : e.Message));
        }
    }

    private static string Took(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} minutes" : $"{t.TotalSeconds:0.#} seconds";

    internal static string FileNameFrom(Uri uri, ContentDispositionHeaderValue? cd)
    {
        var raw = cd?.FileNameStar ?? cd?.FileName;
        if (string.IsNullOrWhiteSpace(raw)) raw = Uri.UnescapeDataString(uri.AbsolutePath);
        return FileNames.Safe(raw);
    }
}
