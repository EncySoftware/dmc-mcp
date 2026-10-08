using System.Security.Cryptography;

namespace DmcMcp;

/// <summary>
/// Files a hosted client sent with POST /mcp/upload, kept for 24 hours under &lt;root&gt;/&lt;id&gt;/ and named by
/// upload:&lt;id&gt; in tool calls. The id is 128 random bits written as 32 hex characters — the only thing a
/// reference may contain, so it can never name a folder outside the store. The disk is the DMC server's, shared
/// with the backend and its database: at most 1 GB a file, 5 GB in all, and two uploads at a time. An upload is
/// its sender's: the key's account, or the person whose token sent it (<see cref="Caller.Owner"/>, kept in .owner);
/// to anyone else its id resolves to nothing, exactly like an id that never existed.
/// </summary>
public sealed class UploadStore(string root, Func<DateTimeOffset>? clock = null)
{
    public const long MaxBytes = 1024L * 1024 * 1024;
    public const int MaxConcurrent = 2;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const string ExpiresFile = ".expires";
    private const string OwnerFile = ".owner";
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    /** Everything the store may hold at once, unexpired uploads together. */
    public long MaxTotalBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    /** The store is full: nothing more until uploads expire. The endpoint answers 507. */
    public sealed class FullException(string message) : IOException(message);

    public sealed record Saved(string Id, string Name, long Size, DateTimeOffset ExpiresAt)
    {
        public string Ref => "upload:" + Id;
    }

    /** A slot for one upload in progress — dispose it when done — or null when MaxConcurrent already run. */
    public IDisposable? TryBegin() => _slots.Wait(0) ? new Slot(_slots) : null;

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release(); }
    }

    /** The uploaded files' bytes now, expired ones not yet purged included. */
    public long UsedBytes()
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!IsBookkeeping(Path.GetFileName(f)))
                try { total += new FileInfo(f).Length; } catch (IOException) { /* deleted meanwhile */ }
        return total;
    }

    internal FullException Full() => new(
        $"the server's upload space is full ({MaxTotalBytes / (1024 * 1024)} MB of uploads from the last 24 hours) — "
        + "pass an https:// link instead, use an upload:<id> already made, or try again later");

    /** The store's own notes beside each upload; an uploaded file never takes one of these names. */
    private static bool IsBookkeeping(string fileName) =>
        fileName.Equals(ExpiresFile, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(OwnerFile, StringComparison.OrdinalIgnoreCase);

    /** An upload of the key's account. */
    public Task<Saved> Save(Stream content, string fileName, CancellationToken ct = default) =>
        Save(content, fileName, Caller.Server.Owner, ct);

    public async Task<Saved> Save(Stream content, string fileName, string owner, CancellationToken ct = default)
    {
        Purge();
        var room = MaxTotalBytes - UsedBytes();
        if (room <= 0) throw Full();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var name = FileNames.Safe(fileName);
        if (IsBookkeeping(name)) name = "_" + name; // a file called .owner must not overwrite the owner
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        try
        {
            // The owner before the bytes: nothing in the store is ever without one.
            await File.WriteAllTextAsync(Path.Combine(dir, OwnerFile), owner, ct);
            long size = 0;
            await using (var dst = File.Create(Path.Combine(dir, name)))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await content.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > MaxBytes) throw new InvalidDataException("the file is larger than 1 GB");
                    if (size > room) throw Full();
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            var expires = _now() + Lifetime;
            await File.WriteAllTextAsync(Path.Combine(dir, ExpiresFile), expires.ToUnixTimeSeconds().ToString(), ct);
            return new Saved(id, name, size, expires);
        }
        catch
        {
            // Refused, cancelled or the disk failed: nothing half-written stays to count against the store.
            try { Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    /** As the key's account. */
    public string? Resolve(string reference) => Resolve(reference, Caller.Server.Owner);

    /// <summary>
    /// upload:&lt;id&gt; → the stored file's path; null when malformed, unknown, expired or another owner's — the
    /// same null for each, so a guessed or leaked id of someone else's says nothing about it.
    /// </summary>
    public string? Resolve(string reference, string owner)
    {
        const string prefix = "upload:";
        reference = reference.Trim();
        if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = reference[prefix.Length..].Trim().ToLowerInvariant();
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return null;
        var dir = Path.Combine(root, id);
        if (!Directory.Exists(dir) || Expired(dir) || !OwnedBy(dir, owner)) return null;
        return Directory.GetFiles(dir).FirstOrDefault(f => !IsBookkeeping(Path.GetFileName(f)));
    }

    private static bool OwnedBy(string dir, string owner)
    {
        var f = Path.Combine(dir, OwnerFile);
        try
        {
            // 0.8.0 wrote no owner: every upload then was made with the key.
            var stored = File.Exists(f) ? File.ReadAllText(f).Trim() : Caller.Server.Owner;
            return string.Equals(stored, owner, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes expired uploads; returns how many went.</summary>
    public int Purge()
    {
        if (!Directory.Exists(root)) return 0;
        var gone = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                if (!Expired(dir)) continue;
                Directory.Delete(dir, true);
                gone++;
            }
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
