# dmc-mcp hosted server — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dmc-mcp serve` — the same 21 tools over MCP Streamable HTTP, behind a key, taking files by upload or https link, running in a container on the DMC VPS for the Hermes agent.

**Architecture:** New small units, each with one job and its own tests: `AddressGuard`/`Downloader` (https fetch that refuses non-public addresses), `UploadStore` (upload:<id> files with a 24 h lifetime), `ZipFolder` (safe unpacking for `publish_folder`), `FileInputs` (turns a tool's file argument into a local path — local path, upload id or link, and refuses local paths when hosted), `KeyAuth` (header or path key), `SignInKeepAlive` (startup account line + 12-hourly refresh), `ServeCommand` (ASP.NET Core host). `DmcTools` only swaps `Path.GetFullPath(file)` for `FileInputs`. The image is the published NuGet tool on `aspnet:8.0` — nothing is compiled on the 2-CPU VPS.

**Tech Stack:** .NET 8, ModelContextProtocol + ModelContextProtocol.AspNetCore 1.4.1, xUnit 2.8, Docker, nginx.

**Spec:** `docs/superpowers/specs/2026-10-06-remote-server-design.md`

## Global Constraints

- Target `net8.0`; MCP SDK packages exactly `1.4.1`.
- The stdio mode, `login`, `setup`, `doctor` and every existing test keep working unchanged; `new DmcTools(client, tokens)` stays valid.
- Hosted mode refuses local paths — no tool may read the container's own files.
- Uploads and downloads: at most 1 GB (`1024L * 1024 * 1024`); uploads live 24 hours; upload ids are 32 lowercase hex chars (128 random bits).
- Downloads: `https://` only, at most 5 redirects, 10 minutes, connect only to public addresses (checked at connect time).
- Zip for `publish_folder`: no absolute or `..` entries, at most 5000 entries, at most 1 GB unpacked.
- Key: env `DMC_MCP_KEY`, at least 32 chars, constant-time comparison; `Authorization: Bearer <key>` or `/mcp/<key>[/...]`.
- Data dir: env `DMC_MCP_DATA` (default `/data`); uploads in `<data>/uploads`; the token file via `XDG_CONFIG_HOME=/data` → `/data/dmc-mcp/auth.json`.
- No password on the server: sign-in is `login --password` run once inside the container.
- Container: name `dmc-mcp`, `127.0.0.1:8095:8080`, env file `/opt/dmc-mcp/dmc-mcp.env`, volume `/opt/dmc-mcp/data:/data` owned by UID 1654 (`app`).
- Everything in this repository is English. Commit messages: English, no `Co-Authored-By` trailer.
- Test command: `dotnet test tests/DmcMcp.Tests.csproj -c Release --nologo` (139 tests pass before this plan).

---

### Task 1: AddressGuard and Downloader

**Files:**
- Create: `src/Downloader.cs`
- Test: `tests/DownloaderTests.cs`

**Interfaces:**
- Produces: `public static class AddressGuard { public static bool IsPublic(IPAddress a); }`
- Produces: `public sealed class Downloader { public Downloader(); internal Downloader(HttpMessageHandler handler); internal long MaxBytes { get; init; } /* default 1 GB */; public Task<(string? Path, string? Error)> Fetch(string url, string intoDir, CancellationToken ct = default); internal static string FileNameFrom(Uri uri, ContentDispositionHeaderValue? cd); }`
- Produces: `public static class FileNames { public static string Safe(string raw); }` (in the same file; used by `UploadStore` too)

- [ ] **Step 1: Write the failing tests** — `tests/DownloaderTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DmcMcp.Tests.csproj -c Release --nologo --filter DownloaderTests`
Expected: build error — `AddressGuard`, `Downloader` do not exist.

- [ ] **Step 3: Implement** — `src/Downloader.cs`:

```csharp
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
    public static string Safe(string? raw)
    {
        var name = (raw ?? "").Trim().Trim('"').Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '<', '>', '|' }))
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "download" : name;
    }
}

/// <summary>
/// Fetches a file the hosted server's caller points at by an https link: at most 5 redirects, 1 GB and 10 minutes,
/// and every connection — redirects included — goes only to a public address, checked when the socket opens, so
/// neither a redirect nor a DNS answer can steer it into the server's own network.
/// </summary>
public sealed class Downloader
{
    internal const long OneGigabyte = 1024L * 1024 * 1024;
    private readonly HttpClient _http;

    internal long MaxBytes { get; init; } = OneGigabyte;

    public Downloader() : this(GuardedHandler()) { }

    internal Downloader(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

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
        string? path = null;
        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, $"ERROR: {uri.Host} answered {(int)resp.StatusCode} for the link.");
            if (resp.Content.Headers.ContentLength > MaxBytes)
                return (null, $"ERROR: the file behind the link is larger than {MaxBytes / (1024 * 1024)} MB.");
            var name = FileNameFrom(resp.RequestMessage?.RequestUri ?? uri, resp.Content.Headers.ContentDisposition);
            Directory.CreateDirectory(intoDir);
            path = Path.Combine(intoDir, name);
            long total = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (total > MaxBytes)
            {
                File.Delete(path);
                return (null, $"ERROR: the file behind the link is larger than {MaxBytes / (1024 * 1024)} MB.");
            }
            return (path, null);
        }
        catch (HttpRequestException e)
        {
            if (path != null) try { File.Delete(path); } catch { }
            return (null, "ERROR: could not download the link — " + (e.InnerException?.Message ?? e.Message));
        }
        catch (TaskCanceledException)
        {
            if (path != null) try { File.Delete(path); } catch { }
            return (null, "ERROR: the download took longer than 10 minutes.");
        }
    }

    internal static string FileNameFrom(Uri uri, ContentDispositionHeaderValue? cd)
    {
        var raw = cd?.FileNameStar ?? cd?.FileName;
        if (string.IsNullOrWhiteSpace(raw)) raw = Uri.UnescapeDataString(uri.AbsolutePath);
        return FileNames.Safe(raw);
    }
}
```

Note: when the refusal comes from `ConnectCallback`, .NET wraps it — the message test checks `"not a public address"`, which survives in `e.InnerException?.Message ?? e.Message`; if a test shows the text one level deeper, walk `InnerException` until it is found.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/DmcMcp.Tests.csproj -c Release --nologo --filter DownloaderTests`
Expected: all pass. Then the whole suite: 139 + new, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Downloader.cs tests/DownloaderTests.cs
git commit -m "Downloader: https links for the hosted server, public addresses only"
```

---

### Task 2: UploadStore

**Files:**
- Create: `src/UploadStore.cs`
- Test: `tests/UploadStoreTests.cs`

**Interfaces:**
- Consumes: `FileNames.Safe(string?)` from Task 1 (if Task 1 is not merged yet, copy that static class verbatim — the merge keeps one).
- Produces: `public sealed class UploadStore { public UploadStore(string root, Func<DateTimeOffset>? clock = null); public const long MaxBytes; public static readonly TimeSpan Lifetime; public sealed record Saved(string Id, string Name, long Size, DateTimeOffset ExpiresAt) { public string Ref => "upload:" + Id; } public Task<Saved> Save(Stream content, string fileName, CancellationToken ct = default); public string? Resolve(string reference); public int Purge(); }`

- [ ] **Step 1: Write the failing tests** — `tests/UploadStoreTests.cs`:

```csharp
using DmcMcp;
using Xunit;

/** upload:<id> — how a hosted client hands the server a file it cannot read from the client's disk. */
public class UploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmc-up-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private UploadStore Store() => new(_root, () => _now);

    [Fact]
    public async Task SavedFileResolvesByItsReference()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 7, 8 }), "Fanuc 0i.sppx");
        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
        Assert.Equal("upload:" + saved.Id, saved.Ref);
        Assert.Equal(2, saved.Size);
        Assert.Equal(_now + TimeSpan.FromHours(24), saved.ExpiresAt);
        var path = Store().Resolve(saved.Ref);
        Assert.Equal("Fanuc 0i.sppx", Path.GetFileName(path));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(path!));
    }

    [Fact]
    public async Task ExpiredUploadIsGoneAndPurged()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        _now += TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);
        Assert.Null(Store().Resolve(saved.Ref));
        Assert.Equal(1, Store().Purge());
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("upload:../../etc")]
    [InlineData("upload:")]
    [InlineData("upload:0123")]
    [InlineData("upload:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("/etc/passwd")]
    [InlineData("upload:0123456789abcdef0123456789abcdef")]
    public void UnknownOrMalformedReferencesResolveToNothing(string reference) =>
        Assert.Null(Store().Resolve(reference));

    [Fact]
    public async Task NameCannotClimbOutOfItsFolder()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "..\\..\\evil.sppx");
        Assert.Equal("evil.sppx", saved.Name);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(Store().Resolve(saved.Ref)!));
    }

    [Fact]
    public async Task ReferenceIsCaseInsensitive()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.zip");
        Assert.NotNull(Store().Resolve("UPLOAD:" + saved.Id.ToUpperInvariant()));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/DmcMcp.Tests.csproj -c Release --nologo --filter UploadStoreTests`
Expected: build error — `UploadStore` does not exist.

- [ ] **Step 3: Implement** — `src/UploadStore.cs`:

```csharp
using System.Security.Cryptography;

namespace DmcMcp;

/// <summary>
/// Files a hosted client sent with POST /mcp/upload, kept for 24 hours under &lt;root&gt;/&lt;id&gt;/ and named by
/// upload:&lt;id&gt; in tool calls. The id is 128 random bits written as 32 hex characters — the only thing a
/// reference may contain, so it can never name a folder outside the store.
/// </summary>
public sealed class UploadStore(string root, Func<DateTimeOffset>? clock = null)
{
    public const long MaxBytes = 1024L * 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const string ExpiresFile = ".expires";
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public sealed record Saved(string Id, string Name, long Size, DateTimeOffset ExpiresAt)
    {
        public string Ref => "upload:" + Id;
    }

    public async Task<Saved> Save(Stream content, string fileName, CancellationToken ct = default)
    {
        Purge();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var name = FileNames.Safe(fileName);
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        long size;
        await using (var dst = File.Create(path))
        {
            await content.CopyToAsync(dst, ct);
            size = dst.Length;
        }
        if (size > MaxBytes)
        {
            Directory.Delete(dir, true);
            throw new InvalidDataException("the file is larger than 1 GB");
        }
        var expires = _now() + Lifetime;
        await File.WriteAllTextAsync(Path.Combine(dir, ExpiresFile), expires.ToUnixTimeSeconds().ToString(), ct);
        return new Saved(id, name, size, expires);
    }

    /// <summary>upload:&lt;id&gt; → the stored file's path; null when malformed, unknown or expired.</summary>
    public string? Resolve(string reference)
    {
        const string prefix = "upload:";
        reference = reference.Trim();
        if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = reference[prefix.Length..].Trim().ToLowerInvariant();
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return null;
        var dir = Path.Combine(root, id);
        if (!Directory.Exists(dir) || Expired(dir)) return null;
        return Directory.GetFiles(dir).FirstOrDefault(f => Path.GetFileName(f) != ExpiresFile);
    }

    /// <summary>Deletes expired uploads; returns how many went.</summary>
    public int Purge()
    {
        if (!Directory.Exists(root)) return 0;
        var gone = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!Expired(dir)) continue;
            try { Directory.Delete(dir, true); gone++; }
            catch (IOException) { /* in use right now — the next purge takes it */ }
            catch (UnauthorizedAccessException) { }
        }
        return gone;
    }

    private bool Expired(string dir)
    {
        var f = Path.Combine(dir, ExpiresFile);
        // No stamp yet means a Save still writing it — or a half-written leftover, which the next day's purge takes.
        if (!File.Exists(f)) return Directory.GetCreationTimeUtc(dir) < _now().UtcDateTime - Lifetime;
        return !long.TryParse(File.ReadAllText(f), out var s) || DateTimeOffset.FromUnixTimeSeconds(s) <= _now();
    }
}
```

- [ ] **Step 4: Run the tests** — `--filter UploadStoreTests` then the whole suite; all pass.

- [ ] **Step 5: Commit**

```bash
git add src/UploadStore.cs tests/UploadStoreTests.cs
git commit -m "UploadStore: upload:<id> files for the hosted server, 24 hours"
```

---

### Task 3: ZipFolder — publish_folder's folder as a zip

**Files:**
- Create: `src/ZipFolder.cs`
- Test: `tests/ZipFolderTests.cs`

**Interfaces:**
- Produces: `public static class ZipFolder { public const int MaxEntries = 5000; public const long MaxBytes = 1024L * 1024 * 1024; public static (string? Dir, string? Error) Extract(string zipPath, string intoDir); }`

- [ ] **Step 1: Write the failing tests** — `tests/ZipFolderTests.cs`:

```csharp
using System.IO.Compression;
using DmcMcp;
using Xunit;

/** On the hosted server publish_folder gets the folder as a zip; unpacking it must stay inside a temp folder. */
public class ZipFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-zf-" + Guid.NewGuid().ToString("N")[..8]);
    public ZipFolderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Zip(params string[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6] + ".zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries)
        {
            var entry = z.CreateEntry(e);
            if (!e.EndsWith('/')) using (var w = new StreamWriter(entry.Open())) w.Write("x");
        }
        return path;
    }

    [Fact]
    public void ContentsZipUnpacksAsTheFolder()
    {
        var (dir, error) = ZipFolder.Extract(Zip("a.sppx", "Haas VF-2/Haas VF-2.xml", "Haas VF-2/Images/base.osd"), Path.Combine(_dir, "out"));
        Assert.Null(error);
        Assert.True(File.Exists(Path.Combine(dir!, "a.sppx")));
        Assert.True(File.Exists(Path.Combine(dir!, "Haas VF-2", "Haas VF-2.xml")));
    }

    /** People zip the folder itself; a single top folder that is not a schema is the folder to publish. */
    [Fact]
    public void ZipOfTheFolderItselfUnpacksIntoIt()
    {
        var (dir, _) = ZipFolder.Extract(Zip("posts/a.sppx", "posts/b.sppx"), Path.Combine(_dir, "out"));
        Assert.Equal("posts", Path.GetFileName(dir));
    }

    /** But a single schema folder (xml directly inside) is one component, not the folder to publish. */
    [Fact]
    public void SingleSchemaFolderStaysAComponent()
    {
        var (dir, _) = ZipFolder.Extract(Zip("Haas VF-2/Haas VF-2.xml", "Haas VF-2/Images/base.osd"), Path.Combine(_dir, "out"));
        Assert.Equal("out", Path.GetFileName(dir));
    }

    [Theory]
    [InlineData("../evil.sppx")]
    [InlineData("a/../../evil.sppx")]
    [InlineData("/etc/evil.sppx")]
    [InlineData("C:/evil.sppx")]
    public void EntriesPointingOutsideAreRefused(string entry)
    {
        var (dir, error) = ZipFolder.Extract(Zip(entry), Path.Combine(_dir, "out"));
        Assert.Null(dir);
        Assert.Contains("points outside", error);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.sppx")));
    }

    [Fact]
    public void NotAZipIsReported()
    {
        var path = Path.Combine(_dir, "x.zip");
        File.WriteAllText(path, "not a zip");
        var (dir, error) = ZipFolder.Extract(path, Path.Combine(_dir, "out"));
        Assert.Null(dir);
        Assert.Contains("not a readable zip", error);
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `--filter ZipFolderTests`; build error.

- [ ] **Step 3: Implement** — `src/ZipFolder.cs`:

```csharp
using System.IO.Compression;

namespace DmcMcp;

/// <summary>
/// publish_folder's folder, sent to the hosted server as a zip: unpacked into a fresh folder with no entry allowed
/// out of it (absolute paths, drive letters, ".." segments), at most 5000 entries and 1 GB unpacked. A zip of the
/// folder itself — one top folder and nothing beside it — publishes that folder, unless that folder is a schema
/// (an .xml directly inside), which is one component.
/// </summary>
public static class ZipFolder
{
    public const int MaxEntries = 5000;
    public const long MaxBytes = 1024L * 1024 * 1024;

    public static (string? Dir, string? Error) Extract(string zipPath, string intoDir)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries)
                return (null, $"ERROR: the zip has {zip.Entries.Count} entries; at most {MaxEntries}.");
            long total = 0;
            foreach (var e in zip.Entries) total += e.Length;
            if (total > MaxBytes) return (null, "ERROR: the zip unpacks to more than 1 GB.");

            var root = Path.GetFullPath(intoDir);
            foreach (var e in zip.Entries)
            {
                var rel = e.FullName.Replace('\\', '/');
                if (rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Contains(".."))
                    return (null, $"ERROR: the zip entry \"{e.FullName}\" points outside the folder.");
            }
            Directory.CreateDirectory(root);
            foreach (var e in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('\\', '/')));
                if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return (null, $"ERROR: the zip entry \"{e.FullName}\" points outside the folder.");
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }

            var top = Directory.GetFileSystemEntries(root);
            if (top.Length == 1 && Directory.Exists(top[0]) && Directory.GetFiles(top[0], "*.xml").Length == 0)
                return (top[0], null);
            return (root, null);
        }
        catch (InvalidDataException) { return (null, "ERROR: the file is not a readable zip."); }
    }
}
```

- [ ] **Step 4: Run the tests** — filter, then the whole suite; all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ZipFolder.cs tests/ZipFolderTests.cs
git commit -m "ZipFolder: publish_folder's folder as a zip, unpacked without escaping"
```

---

### Task 4: Sign-in for a server — one refresh at a time, startup line, keep-alive

**Files:**
- Modify: `src/DmcTokenProvider.cs` (the refresh in `GetAccessToken`, `AuthFilePath`, the static `Http`)
- Create: `src/SignInKeepAlive.cs`
- Test: `tests/DmcTokenProviderTests.cs` (add to the existing class), `tests/SignInKeepAliveTests.cs`

**Interfaces:**
- Produces: `DmcTokenProvider.AuthFilePath` honours env `DMC_AUTH_FILE` when set; `internal HttpClient Client { get; init; }` (defaults to the shared static client).
- Produces: `public sealed class SignInKeepAlive(IDmcClient dmc, DmcTokenProvider tokens, ILogger<SignInKeepAlive> log) : BackgroundService` and `internal static Task<string> Describe(IDmcClient dmc, DmcTokenProvider tokens)`.
- Produces: `public const string HostedLoginCommand = "docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password";` on `SignInKeepAlive`.

Why the lock: over HTTP two tool calls can arrive together; both see an expired access token, both refresh with the same refresh token, and with Keycloak's refresh-token rotation the second refresh fails — the server would report "sign-in expired" at random.

- [ ] **Step 1: Write the failing tests** — append to `tests/DmcTokenProviderTests.cs` inside the class:

```csharp
    /** Over HTTP two calls arrive together: one refresh, not two (rotation would kill the second). */
    [Fact]
    public async Task ConcurrentCallsRefreshOnce()
    {
        var file = Path.Combine(Path.GetTempPath(), "dmc-auth-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(file, """{"refresh_token":"r1","client_id":"dealer-space"}""");
        Environment.SetEnvironmentVariable("DMC_AUTH_FILE", file);
        try
        {
            var handler = new CountingKeycloak();
            var provider = new DmcTokenProvider { Client = new HttpClient(handler) };
            var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAccessToken()));
            Assert.All(tokens, t => Assert.Equal("a1", t));
            Assert.Equal(1, handler.Calls);
            Assert.Contains("r2", File.ReadAllText(file)); // the rotated refresh token is kept
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMC_AUTH_FILE", null);
            File.Delete(file);
        }
    }

    private sealed class CountingKeycloak : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(50, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"a1","expires_in":300,"refresh_token":"r2"}"""),
            };
        }
    }
```

`tests/SignInKeepAliveTests.cs`:

```csharp
using DmcMcp;
using Xunit;

/** The hosted server's log says once at startup whom it acts as — the only check its operator needs. */
public class SignInKeepAliveTests
{
    [Fact]
    public async Task NotSignedInPointsAtTheCommand()
    {
        var line = await SignInKeepAlive.Describe(new FakeDmcClient(), new FakeTokens(null));
        Assert.Contains("NOT signed in", line);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, line);
    }

    [Fact]
    public async Task PublisherIsReported()
    {
        var client = new FakeDmcClient { MeResult = new MeInfo("hermes@encycam.com", new[] { "DEALER" }) };
        var line = await SignInKeepAlive.Describe(client, new FakeTokens("tok"));
        Assert.Contains("hermes@encycam.com", line);
        Assert.DoesNotContain("WARNING", line);
    }

    [Fact]
    public async Task MissingPublisherRoleIsAWarning()
    {
        var client = new FakeDmcClient { MeResult = new MeInfo("hermes@encycam.com", new[] { "USER" }) };
        var line = await SignInKeepAlive.Describe(client, new FakeTokens("tok"));
        Assert.Contains("WARNING", line);
        Assert.Contains("Publisher", line);
    }
}
```

Before writing these, read `tests/Fakes.cs` and `src/DmcClient.cs` for the real shapes of `FakeDmcClient`'s `Me` stub and of `MeInfo` (`MeInfo(Username, Roles)` in `DmcClient.cs`); if `FakeDmcClient` has no settable `Me` result, add `public MeInfo MeResult { get; set; } = new("user", new[] { "DEALER" });` and return it from its `Me`. Adjust the constructor calls above to the real `MeInfo` signature.

- [ ] **Step 2: Run to verify they fail** — `--filter "DmcTokenProviderTests|SignInKeepAliveTests"`; build errors (`Client`, `SignInKeepAlive`).

- [ ] **Step 3: Implement.** In `src/DmcTokenProvider.cs`:

1. `AuthFilePath` honours `DMC_AUTH_FILE`:

```csharp
    public static string AuthFilePath =>
        Environment.GetEnvironmentVariable("DMC_AUTH_FILE") is { Length: > 0 } file
            ? file
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Brand.AuthFolder, "auth.json");
```

2. Add below the static `Http`:

```csharp
    /** The HTTP client for Keycloak — replaced in tests. */
    internal HttpClient Client { get; init; } = Http;

    /** One refresh at a time: with refresh-token rotation, a second refresh with the same token fails. */
    private readonly SemaphoreSlim _refreshing = new(1, 1);
```

3. In `GetAccessToken`, after the env and cache checks, wrap the refresh in the gate and re-check the cache inside it; use `Client` instead of `Http` for the refresh POST (leave the browser and console sign-in flows on `Http`):

```csharp
        if (_cachedAccess != null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccess;
        await _refreshing.WaitAsync();
        try
        {
            if (_cachedAccess != null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccess;
            var stored = ReadStored();
            if (stored.Refresh == null) return null;
            // ... the existing refresh code, unchanged except `Http.PostAsync` -> `Client.PostAsync` ...
            return _cachedAccess;
        }
        finally { _refreshing.Release(); }
```

`src/SignInKeepAlive.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DmcMcp;

/// <summary>
/// The hosted server signs in once (`login --password` inside the container) and keeps only an offline refresh
/// token. An offline session dies when idle, so this refreshes it every 12 hours whether or not anyone calls the
/// server, and says at startup whom the server acts as — the line an operator looks for in `docker logs`.
/// </summary>
public sealed class SignInKeepAlive(IDmcClient dmc, DmcTokenProvider tokens, ILogger<SignInKeepAlive> log) : BackgroundService
{
    public const string HostedLoginCommand = "docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password";
    internal static readonly TimeSpan Every = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            log.LogWarning("{Line}", await Describe(dmc, tokens));
            try { await Task.Delay(Every, stop); }
            catch (TaskCanceledException) { return; }
        }
    }

    internal static async Task<string> Describe(IDmcClient dmc, DmcTokenProvider tokens)
    {
        string? token;
        try { token = await tokens.GetAccessToken(); }
        catch (InvalidOperationException e) { return $"DMC sign-in is broken ({e.Message}) — run: {HostedLoginCommand}"; }
        if (token == null) return $"NOT signed in to DMC — run once: {HostedLoginCommand}";
        try
        {
            var me = await dmc.Me(token);
            var roles = me.Roles.Count == 0 ? "none" : string.Join(", ", me.Roles);
            var line = $"Signed in to DMC as {me.Username}, roles: {roles}";
            return me.Roles.Contains("DEALER", StringComparer.OrdinalIgnoreCase)
                ? line
                : line + " — WARNING: no Publisher role, publishing will be refused; an admin grants it in DMC.";
        }
        catch (Exception e) { return "Signed in, but DMC did not answer /auth/me: " + e.Message; }
    }
}
```

(`LogWarning` so the line survives the Warning level the server sets for ASP.NET's own noise.)

- [ ] **Step 4: Run the tests** — filter, then the whole suite; all pass.

- [ ] **Step 5: Commit**

```bash
git add src/DmcTokenProvider.cs src/SignInKeepAlive.cs tests/DmcTokenProviderTests.cs tests/SignInKeepAliveTests.cs tests/Fakes.cs
git commit -m "Sign-in for a server: one refresh at a time, a startup line, a 12-hourly keep-alive"
```

---

### Task 5: FileInputs and the tools that take files

**Files:**
- Create: `src/FileInputs.cs`
- Modify: `src/DmcTools.cs` — constructor; `PublishPost` (~line 70), `ReplacePostFile` (~251), `PublishFolder` (~312 and the manifest at ~327), `InspectArchive` (~849), `SetCover` (~860), `Token()` (~1139), the `file`/`dir`/`manifest` parameter descriptions
- Test: `tests/FileInputsTests.cs`, `tests/HostedToolsTests.cs`

**Interfaces:**
- Consumes: `UploadStore` (Task 2), `Downloader` (Task 1), `ZipFolder` (Task 3), `SignInKeepAlive.HostedLoginCommand` (Task 4).
- Produces:

```csharp
public sealed class FileInputs
{
    public FileInputs(bool hosted, UploadStore? uploads = null, Downloader? downloader = null, string? tempRoot = null);
    public static FileInputs Local { get; }
    public bool Hosted { get; }
    public sealed class Input : IDisposable { public string? Path { get; } public string? Error { get; } }
    public Task<Input> File(string arg, CancellationToken ct = default);
    public Task<Input> Folder(string arg, CancellationToken ct = default);
    internal const string HostedPathRefusal;
}
public class DmcTools(IDmcClient dmc, DmcTokenProvider tokens, FileInputs? inputs = null)
```

- [ ] **Step 1: Write the failing tests.** `tests/FileInputsTests.cs`:

```csharp
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
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        using var input = await Hosted().File(saved.Ref);
        Assert.Null(input.Error);
        Assert.Equal("a.sppx", Path.GetFileName(input.Path));
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

    [Fact]
    public async Task HostedFolderPathIsRefused()
    {
        using var input = await Hosted().Folder("/data");
        Assert.Equal(FileInputs.HostedPathRefusal, input.Error);
    }
}
```

`tests/HostedToolsTests.cs` — the tools themselves on a hosted `FileInputs` (read `tests/PublishPostTests.cs` and `tests/InspectArchiveTests.cs` first and reuse their arrangement: how `FakeDmcClient` records `StartImport` and how a minimal component zip is built):

```csharp
using DmcMcp;
using Xunit;

/** The same tools on the hosted server: paths refused, upload ids accepted, sign-in hint names the container. */
public class HostedToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-ht-" + Guid.NewGuid().ToString("N")[..8]);
    public HostedToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UploadStore Uploads() => new(Path.Combine(_dir, "uploads"));
    private DmcTools Tools(FakeDmcClient client, string? token = "tok") =>
        new(client, new FakeTokens(token), new FileInputs(true, Uploads(), tempRoot: Path.Combine(_dir, "tmp")));

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
        await Tools(client).PublishPost(saved.Ref, force: true);
        // Assert with whatever FakeDmcClient records for StartImport (see PublishPostTests): the imported file is
        // the stored one — its name is Fanuc.sppx and it lives under the uploads folder.
        Assert.EndsWith("Fanuc.sppx", client.LastImportedFile);
    }

    [Fact]
    public async Task NotSignedInNamesTheContainerCommand()
    {
        var saved = await Uploads().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        var answer = await Tools(new FakeDmcClient(), token: null).PublishPost(saved.Ref);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, answer);
    }
}
```

If `FakeDmcClient` does not already record the imported file, add `public string? LastImportedFile { get; private set; }` set in its `StartImport`. Use the real parameter list of `PublishFolder` (named arguments as above).

- [ ] **Step 2: Run to verify they fail** — `--filter "FileInputsTests|HostedToolsTests"`; build errors.

- [ ] **Step 3: Implement `src/FileInputs.cs`:**

```csharp
namespace DmcMcp;

/// <summary>
/// Turns a tool's file argument into a local path. At home that is the path itself (an https link works too). On
/// the hosted server it is never a path — the server would be reading its own disk, and /proc/self/environ holds
/// its key — but an upload:&lt;id&gt; from POST /mcp/upload or an https link. A folder (publish_folder) arrives on
/// the hosted server as a zip. Temporary copies go away when the Input is disposed.
/// </summary>
public sealed class FileInputs(bool hosted, UploadStore? uploads = null, Downloader? downloader = null, string? tempRoot = null)
{
    public static FileInputs Local { get; } = new(false);

    internal const string HostedPathRefusal =
        "ERROR: this is the hosted DMC server — it cannot read paths, neither yours nor its own. Upload the file " +
        "first (POST <server>/mcp/upload with the same key, multipart field \"file\") and pass the upload:<id> it " +
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
            try { Directory.Delete(_cleanup, true); } catch { /* best effort: the OS temp cleaner takes the rest */ }
        }
    }

    public async Task<Input> File(string arg, CancellationToken ct = default)
    {
        arg = arg.Trim();
        if (arg.StartsWith("upload:", StringComparison.OrdinalIgnoreCase))
        {
            if (uploads == null)
                return new Input(null, "ERROR: upload:<id> works only on the hosted server — here pass the file's path.");
            var stored = uploads.Resolve(arg);
            return stored == null
                ? new Input(null, $"ERROR: {arg} is unknown or expired (uploads live 24 hours) — upload the file again.")
                : new Input(stored, null);
        }
        if (arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var dir = NewTemp();
            var (path, error) = await _downloader.Fetch(arg, dir, ct);
            return path == null ? Cleaned(error!, dir) : new Input(path, null, dir);
        }
        if (hosted) return new Input(null, HostedPathRefusal);
        var full = System.IO.Path.GetFullPath(arg);
        return System.IO.File.Exists(full) ? new Input(full, null) : new Input(null, $"ERROR: file {full} does not exist.");
    }

    public async Task<Input> Folder(string arg, CancellationToken ct = default)
    {
        arg = arg.Trim();
        var reference = arg.StartsWith("upload:", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!reference)
        {
            if (hosted) return new Input(null, HostedPathRefusal);
            var full = System.IO.Path.GetFullPath(arg);
            return Directory.Exists(full) ? new Input(full, null) : new Input(null, $"ERROR: folder {full} does not exist.");
        }
        using var zip = await File(arg, ct);
        if (zip.Error != null) return new Input(null, zip.Error);
        var dir = NewTemp();
        var (root, error) = ZipFolder.Extract(zip.Path!, dir);
        return root == null ? Cleaned(error!, dir) : new Input(root, null, dir);
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
```

- [ ] **Step 4: Wire it into `src/DmcTools.cs`.**

Constructor (primary constructor plus a field):

```csharp
public class DmcTools(IDmcClient dmc, DmcTokenProvider tokens, FileInputs? inputs = null)
{
    private readonly FileInputs _inputs = inputs ?? FileInputs.Local;
```

Next to `NoLogin` add:

```csharp
    internal const string HostedNoLogin =
        "ERROR: the hosted DMC server is not signed in — its operator runs `" + SignInKeepAlive.HostedLoginCommand + "` once.";
```

and in `Token()` use `_inputs.Hosted ? HostedNoLogin : NoLogin` where it now returns `NoLogin`.

`PublishPost`, `ReplacePostFile`, `SetCover` — replace the two lines

```csharp
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ERROR: file {path} does not exist.";
```

with

```csharp
        using var input = await _inputs.File(file);
        if (input.Error != null) return input.Error;
        var path = input.Path!;
```

(the `using` keeps a downloaded temp file alive until the method returns — `PublishPost` uploads it during the import).

`InspectArchive` becomes:

```csharp
    public async Task<string> InspectArchive(
        [Description("The archive (.zip / .sppx / .dll / .stnci): a local path, or on the hosted server upload:<id> or an https:// link")] string file)
    {
        using var input = await _inputs.File(file);
        return input.Error ?? ArchiveInspector.Inspect(input.Path!);
    }
```

`PublishFolder` — replace

```csharp
        var dirPath = Path.GetFullPath(dir);
        if (!Directory.Exists(dirPath)) return $"ERROR: folder {dirPath} does not exist.";
```

with

```csharp
        using var folder = await _inputs.Folder(dir);
        if (folder.Error != null) return folder.Error;
        var dirPath = folder.Path!;
```

and the manifest lookup

```csharp
            var mp = Path.GetFullPath(manifest);
            if (!File.Exists(mp)) return $"ERROR: manifest {mp} not found.";
```

with — a bare name inside the folder first (works in both modes, and is how a zipped folder carries its manifest), then `FileInputs.File`:

```csharp
            using var manifestInput = await ManifestInput(manifest, dirPath);
            if (manifestInput.Error != null) return manifestInput.Error;
            var mp = manifestInput.Path!;
```

plus the helper in the class:

```csharp
    /** A manifest named relative to the folder (no path escape), else a path / upload id / link like any file. */
    private async Task<FileInputs.Input> ManifestInput(string manifest, string dirPath)
    {
        var inFolder = Path.GetFullPath(Path.Combine(dirPath, manifest));
        if (inFolder.StartsWith(Path.GetFullPath(dirPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(inFolder))
            return await FileInputs.Local.File(inFolder);
        return await _inputs.File(manifest);
    }
```

Descriptions (the agent learns the hosted route from them):
- `PublishPost` `file`: `"The component file (.sppx, .dll, .stnci or .zip): a local path, or on the hosted server upload:<id> (from POST /mcp/upload) or an https:// link"`
- `ReplacePostFile` `file`: `"The new file (.sppx, .dll, .stnci or .zip): a local path, or on the hosted server upload:<id> or an https:// link"`
- `SetCover` `file`: `"The picture (.png, .jpg or .webp): a local path, or on the hosted server upload:<id> or an https:// link"`
- `PublishFolder` `dir`: `"Folder with components: post files (.sppx, .dll, .stnci, .zip) and subfolders — a schema (xml + osd) or a kit. On the hosted server: the folder as a zip, upload:<id> or an https:// link"`
- `PublishFolder` `manifest`: prepend `"A file name inside the folder, or a path / upload:<id> / https:// link to "` to the existing CSV description.

- [ ] **Step 5: Run the tests** — filter, then the whole suite (139 old + new): all pass. Old tests prove the home mode is unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/FileInputs.cs src/DmcTools.cs tests/FileInputsTests.cs tests/HostedToolsTests.cs tests/Fakes.cs
git commit -m "Tools take upload:<id> and https links; the hosted server reads no paths"
```

---

### Task 6: KeyAuth, the upload endpoint and `dmc-mcp serve`

**Files:**
- Create: `src/KeyAuth.cs`, `src/ServeCommand.cs`
- Modify: `src/Program.cs` (dispatch `serve` before the stdio host), `src/DmcMcp.csproj` (packages)
- Test: `tests/KeyAuthTests.cs`, `tests/ServeTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `public static class KeyAuth { public static (bool Ok, string Path) Check(string path, string? authorization, string key); }`
- Produces: `public static class ServeCommand { public static Task<int> Run(string[] args); internal static WebApplication Build(string[] args, string key, string dataDir); internal const string Instructions; }`

- [ ] **Step 1: Add the packages** to `src/DmcMcp.csproj` next to the existing references:

```xml
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.4.1" />
```

and a new item group:

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

Run `dotnet build src -c Release` — must succeed.

- [ ] **Step 2: Write the failing tests.** `tests/KeyAuthTests.cs`:

```csharp
using DmcMcp;
using Xunit;

/** Who may call the hosted server: the key, by header or as the first path segment for URL-only clients. */
public class KeyAuthTests
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact] public void BearerHeaderLetsIn() => Assert.Equal((true, "/mcp"), KeyAuth.Check("/mcp", "Bearer " + Key, Key));
    [Fact] public void KeyInPathLetsInAndIsStripped() => Assert.Equal((true, "/mcp"), KeyAuth.Check("/mcp/" + Key, null, Key));
    [Fact] public void KeyInPathBeforeUpload() => Assert.Equal((true, "/mcp/upload"), KeyAuth.Check("/mcp/" + Key + "/upload", null, Key));
    [Fact] public void UploadWithHeader() => Assert.Equal((true, "/mcp/upload"), KeyAuth.Check("/mcp/upload", "Bearer " + Key, Key));
    [Fact] public void NoKeyIsOut() => Assert.False(KeyAuth.Check("/mcp", null, Key).Ok);
    [Fact] public void WrongHeaderIsOut() => Assert.False(KeyAuth.Check("/mcp", "Bearer nope", Key).Ok);
    [Fact] public void WrongPathKeyIsOut() => Assert.False(KeyAuth.Check("/mcp/" + Key[..^1] + "0", null, Key).Ok);
    [Fact] public void KeyPrefixIsNotTheKey() => Assert.False(KeyAuth.Check("/mcp/" + Key + "x", null, Key).Ok);
    [Fact] public void OtherPathsAreNotTheServers() => Assert.False(KeyAuth.Check("/api/products", "Bearer " + Key, Key).Ok);
}
```

`tests/ServeTests.cs` — the real server on a free local port, no DMC needed for these calls:

```csharp
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
        Assert.Contains("\\u0022file\\u0022", await resp.Content.ReadAsStringAsync().ContinueWith(t => t.Result.Replace("\"file\"", "\\u0022file\\u0022")));
    }
}
```

(If `_app.Urls.First()` still shows port 0 after start, read the bound address from `_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()` instead.)

- [ ] **Step 3: Run to verify they fail** — `--filter "KeyAuthTests|ServeTests"`; build errors.

- [ ] **Step 4: Implement.** `src/KeyAuth.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace DmcMcp;

/// <summary>
/// The hosted server's door: requests under /mcp carry the key as `Authorization: Bearer &lt;key&gt;`, or — for
/// clients configured by URL alone — as the first path segment, /mcp/&lt;key&gt;[/…], which is stripped before
/// routing. Comparisons are constant-time. Nothing outside /mcp is the server's.
/// </summary>
public static class KeyAuth
{
    public static (bool Ok, string Path) Check(string path, string? authorization, string key)
    {
        if (path != "/mcp" && !path.StartsWith("/mcp/", StringComparison.Ordinal)) return (false, path);
        if (authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && Same(authorization["Bearer ".Length..].Trim(), key))
            return (true, path);
        if (path.StartsWith("/mcp/", StringComparison.Ordinal))
        {
            var rest = path["/mcp/".Length..];
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (Same(segment, key)) return (true, "/mcp" + (slash < 0 ? "" : rest[slash..]));
        }
        return (false, path);
    }

    private static bool Same(string given, string key) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
}
```

`src/ServeCommand.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DmcMcp;

/// <summary>
/// `dmc-mcp serve`: the same tools over MCP Streamable HTTP for agents that cannot start a local process — Hermes
/// on the DMC server. Key at the door (KeyAuth), files by upload or link (FileInputs), one DMC account signed in
/// once inside the container (SignInKeepAlive). Configuration: DMC_MCP_KEY (required, 32+ chars), DMC_MCP_DATA
/// (default /data), ASPNETCORE_HTTP_PORTS / --urls for the port.
/// </summary>
public static class ServeCommand
{
    internal const string Instructions =
        "Hosted Digital Machine Center server. It cannot read file paths. To pass a file (a post, schema, kit, " +
        "cover or a zipped folder for publish_folder), first upload it to this server: POST <this server's MCP " +
        "URL>/upload as multipart/form-data with the field \"file\" and the same key (Authorization: Bearer <key>, " +
        "or the key in the URL path as for MCP). The answer's \"file\" value, upload:<id>, goes into the tool's " +
        "file argument; uploads live 24 hours. An https:// link to the file works too.";

    public static async Task<int> Run(string[] args)
    {
        var key = Environment.GetEnvironmentVariable("DMC_MCP_KEY")?.Trim();
        if (key is null || key.Length < 32)
        {
            Console.Error.WriteLine("ERROR: set DMC_MCP_KEY to a random key of at least 32 characters (openssl rand -hex 32).");
            return 1;
        }
        var data = Environment.GetEnvironmentVariable("DMC_MCP_DATA") is { Length: > 0 } d ? d : "/data";
        await Build(args, key, data).RunAsync();
        return 0;
    }

    internal static WebApplication Build(string[] args, string key, string dataDir)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The key may travel in the path: keep ASP.NET from logging request lines.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = UploadStore.MaxBytes + 1024 * 1024);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadStore.MaxBytes + 1024 * 1024);

        var uploads = new UploadStore(Path.Combine(dataDir, "uploads"));
        builder.Services.AddSingleton(uploads);
        builder.Services.AddSingleton(new FileInputs(true, uploads, new Downloader(), Path.Combine(dataDir, "tmp")));
        builder.Services.AddSingleton<IDmcClient, DmcClient>();
        builder.Services.AddSingleton<DmcTokenProvider>();
        builder.Services.AddSingleton<DmcTools>();
        builder.Services.AddHostedService<SignInKeepAlive>();
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = Instructions)
            .WithHttpTransport()
            .WithTools<DmcTools>();

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var (ok, path) = KeyAuth.Check(ctx.Request.Path.Value ?? "", ctx.Request.Headers.Authorization.ToString(), key);
            if (!ok)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            ctx.Request.Path = path;
            await next();
        });
        app.MapPost("/mcp/upload", async (HttpRequest req, UploadStore store, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "send the file as multipart/form-data, field \"file\"" });
            var form = await req.ReadFormAsync(ct);
            var file = form.Files["file"];
            if (file == null)
                return Results.BadRequest(new { error = "send the file as multipart/form-data, field \"file\"" });
            try
            {
                await using var stream = file.OpenReadStream();
                var saved = await store.Save(stream, file.FileName, ct);
                return Results.Ok(new { file = saved.Ref, name = saved.Name, size = saved.Size, expiresAt = saved.ExpiresAt });
            }
            catch (InvalidDataException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        app.MapMcp("/mcp");
        return app;
    }
}
```

Note: `McpServerOptions.ServerInstructions` is the SDK's name for the `instructions` field of `initialize`; if 1.4.1 names it differently, use the property that lands in `InitializeResult.Instructions` (check with the compiler and the `KeyInThePathReachesMcp` test, which asserts the word "upload" in the initialize answer).

`src/Program.cs` — add before `var builder = Host.CreateApplicationBuilder(args);`:

```csharp
// `dmc-mcp serve` — the hosted server over HTTP (Hermes on the DMC server); see ServeCommand.
if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
    return await ServeCommand.Run(args[1..]);
```

- [ ] **Step 5: Run the tests** — filter, then the whole suite: all pass.

- [ ] **Step 6: Smoke by hand** (Git Bash):

```bash
cd src && DMC_MCP_KEY=0123456789abcdef0123456789abcdef0123456789abcdef DMC_MCP_DATA=$TEMP/dmc-smoke dotnet run -c Release -- serve --urls http://127.0.0.1:5098 &
sleep 8
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:5098/mcp                           # 401
curl -s -X POST http://127.0.0.1:5098/mcp/0123456789abcdef0123456789abcdef0123456789abcdef \
  -H "Accept: application/json, text/event-stream" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}' | head -c 400
kill %1
```

Expected: `401`, then an initialize result naming `dmc-mcp` with the upload instructions.

- [ ] **Step 7: Commit**

```bash
git add src/KeyAuth.cs src/ServeCommand.cs src/Program.cs src/DmcMcp.csproj tests/KeyAuthTests.cs tests/ServeTests.cs
git commit -m "dmc-mcp serve: the tools over HTTP behind a key, with uploads"
```

---

### Task 7: Image, deploy script, README, version 0.8.0

**Files:**
- Create: `Dockerfile`, `deploy/update.sh`
- Modify: `README.md` (a "Hosted server" section), `src/DmcMcp.csproj` (`<Version>0.8.0</Version>`)

- [ ] **Step 1: Check the package layout the image relies on**

```bash
curl -sL -o /tmp/p.nupkg https://api.nuget.org/v3-flatcontainer/encysoftware.dmcmcp/0.7.0/encysoftware.dmcmcp.0.7.0.nupkg
unzip -l /tmp/p.nupkg | grep -E "tools/net8.0/any/(dmc-mcp\.dll|dmc-mcp\.runtimeconfig\.json)"
```

Expected: both files listed. (If the folder differs, use the real one in the Dockerfile below.)

- [ ] **Step 2: `Dockerfile`:**

```dockerfile
# The hosted dmc-mcp (`dmc-mcp serve`): the dotnet tool exactly as published on nuget.org, on the ASP.NET Core
# runtime. Nothing is compiled — the DMC VPS has two cores and its memory belongs to the backend.
#   docker build --build-arg VERSION=0.8.0 -t dmc-mcp:0.8.0 https://github.com/EncySoftware/dmc-mcp.git#v0.8.0
FROM alpine:3.20 AS package
ARG VERSION
RUN test -n "$VERSION" \
 && wget -qO /p.nupkg "https://api.nuget.org/v3-flatcontainer/encysoftware.dmcmcp/${VERSION}/encysoftware.dmcmcp.${VERSION}.nupkg" \
 && mkdir /x && unzip -q /p.nupkg 'tools/net8.0/any/*' -d /x \
 && mv /x/tools/net8.0/any /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
COPY --from=package /app /app
# XDG_CONFIG_HOME puts the sign-in (auth.json) on the volume; uploads go there too.
ENV XDG_CONFIG_HOME=/data DMC_MCP_DATA=/data ASPNETCORE_HTTP_PORTS=8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown app /data
VOLUME /data
USER app
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/dmc-mcp.dll"]
CMD ["serve"]
```

- [ ] **Step 3: `deploy/update.sh`:**

```sh
#!/bin/sh
# On the DMC server, as root: build the image for a released version and replace the dmc-mcp container.
#   sh update.sh 0.8.0
# First time only (see README, "Hosted server"): /opt/dmc-mcp/dmc-mcp.env with DMC_MCP_KEY, the data folder owned
# by UID 1654, then sign in once with the login command the container prints.
set -eu
V="${1:?usage: update.sh <version>, e.g. 0.8.0}"
docker build --build-arg VERSION="$V" -t "dmc-mcp:$V" "https://github.com/EncySoftware/dmc-mcp.git#v$V"
docker rm -f dmc-mcp 2>/dev/null || true
docker run -d --name dmc-mcp --restart unless-stopped \
  -p 127.0.0.1:8095:8080 \
  --env-file /opt/dmc-mcp/dmc-mcp.env \
  -v /opt/dmc-mcp/data:/data \
  "dmc-mcp:$V"
sleep 5
docker logs --tail 20 dmc-mcp
```

- [ ] **Step 4: README** — add a section after the existing install/usage sections:

```markdown
## Hosted server (for agents that cannot run a local process)

`dmc-mcp serve` runs the same tools over MCP Streamable HTTP. One DMC account (a publisher) is signed in once on
the server; callers need the server's key.

- **URL:** `https://dmc.encycam.com/mcp` with `Authorization: Bearer <key>`, or `https://dmc.encycam.com/mcp/<key>`
  for clients that take a URL only.
- **Files:** the server cannot read paths. Upload first and pass the returned `upload:<id>` (24 hours), or pass an
  https link. `publish_folder` takes the folder as a zip.

      curl -H "Authorization: Bearer <key>" -F file=@post.sppx https://dmc.encycam.com/mcp/upload
      {"file":"upload:3f0c…","name":"post.sppx","size":51234,"expiresAt":"…"}

**Running it (operators):** on the server, once —

    mkdir -p /opt/dmc-mcp/data && chown 1654:1654 /opt/dmc-mcp/data
    printf 'DMC_MCP_KEY=%s\n' "$(openssl rand -hex 32)" > /opt/dmc-mcp/dmc-mcp.env && chmod 600 /opt/dmc-mcp/dmc-mcp.env
    sh deploy/update.sh 0.8.0
    docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password    # the server's DMC account

then `sh deploy/update.sh <version>` for every release. `docker logs dmc-mcp` says whom the server is signed in as.
nginx proxies `/mcp` to `127.0.0.1:8095` without buffering, with 15-minute timeouts and no access log.
```

- [ ] **Step 5: Version** — `src/DmcMcp.csproj`: `<Version>0.8.0</Version>`. Run the whole suite and `dotnet pack src -c Release -o pkg` (then delete `pkg/`).

- [ ] **Step 6: Commit**

```bash
git add Dockerfile deploy/update.sh README.md src/DmcMcp.csproj
git commit -m "0.8.0: dmc-mcp serve — the hosted server, its image and deploy script"
```

---

### Task 8: nginx in the DMC frontend repository

**Files:**
- Modify: `C:\spryt\digital-twins-library-frontend\nginx-kc-proxy.conf` (included at server level of the prod vhost `/etc/nginx/sites-enabled/dtl`; the frontend's Jenkins deploy copies it to `/etc/nginx/snippets/kc-proxy.conf` and reloads after `nginx -t`)

- [ ] **Step 1: Check where the snippet is included** (read-only): `ssh root@178.104.57.61 'grep -n "kc-proxy" /etc/nginx/sites-enabled/*'` — expected only `dtl`. If other vhosts include it, say so in the commit message (the location would answer there too, behind the same key).

- [ ] **Step 2: Append to `nginx-kc-proxy.conf`:**

```nginx
# dmc-mcp — the hosted MCP server for agents (EncySoftware/dmc-mcp, Docker on 127.0.0.1:8095). The key may sit in
# the path, so these requests are not logged; imports stream progress for up to 10 minutes, so nothing is buffered.
location = /mcp {
    proxy_pass http://127.0.0.1:8095;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Connection "";
    proxy_buffering off;
    proxy_request_buffering off;
    proxy_read_timeout 900s;
    proxy_send_timeout 900s;
    client_max_body_size 1g;
    access_log off;
}
location ^~ /mcp/ {
    proxy_pass http://127.0.0.1:8095;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Connection "";
    proxy_buffering off;
    proxy_request_buffering off;
    proxy_read_timeout 900s;
    proxy_send_timeout 900s;
    client_max_body_size 1g;
    access_log off;
}
```

- [ ] **Step 3: Validate before pushing** — copy the vhost and the new snippet to a temp location on the server and run `nginx -t -c` against a copy, or rely on the deploy's own `nginx -t` (which keeps the old config on failure). Do not push until the dmc-mcp container is up, so `/mcp` never answers 502 to Danil.

- [ ] **Step 4: Commit and push** (frontend repo, `master`):

```bash
git add nginx-kc-proxy.conf
git commit -m "nginx: /mcp proxies to the hosted dmc-mcp container"
git push origin master
```

---

### Task 9: Release and deploy (with the operator)

Prod changes are confirmed with Lenar before each step; the sign-in is typed by him.

- [ ] Push `main`, tag `v0.8.0` (annotated, English message), wait for `publish-tool` and the package on nuget.org (`curl -s https://api.nuget.org/v3-flatcontainer/encysoftware.dmcmcp/index.json` lists `0.8.0`).
- [ ] On the server: the first-time commands from the README (data folder, key file), `sh update.sh 0.8.0`.
- [ ] Lenar: `docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password` with Hermes's account (a licsys user with the Publisher role; no two-factor, no pending required actions).
- [ ] `docker logs dmc-mcp` shows "Signed in to DMC as …, roles: … DEALER".
- [ ] Task 8's nginx commit; after the frontend deploy: `curl -s -o /dev/null -w "%{http_code}" -X POST https://dmc.encycam.com/mcp` → 401; initialize with the key → 200; an upload → `upload:<id>`; `find_posts` through MCP returns posts.
- [ ] Give Danil the URL and the key (privately), plus the upload command.
```
