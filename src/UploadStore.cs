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
