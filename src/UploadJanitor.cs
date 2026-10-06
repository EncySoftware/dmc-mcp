using Microsoft.Extensions.Hosting;

namespace DmcMcp;

/// <summary>
/// Keeps the hosted server's disk — the DMC server's, shared with the backend and its database — from filling up
/// with what nobody needs any more. Every hour: expired uploads (Save purges too, but only when someone uploads)
/// and temp folders untouched for a day — a download or an unpacked zip whose call was killed mid-way. At startup
/// <see cref="ClearTemp"/> empties the temp folder: no call is alive yet to own anything in it.
/// </summary>
public sealed class UploadJanitor(UploadStore uploads, string tempRoot) : BackgroundService
{
    internal static readonly TimeSpan Every = TimeSpan.FromHours(1);
    /** Far beyond the longest call: a 10-minute download, unpacking, a 10-minute import. */
    internal static readonly TimeSpan TempLifetime = TimeSpan.FromHours(24);

    /** Replaced in tests. */
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public static void ClearTemp(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return;
        foreach (var entry in Directory.GetFileSystemEntries(tempRoot)) Delete(entry);
    }

    internal void Sweep()
    {
        try { uploads.Purge(); } catch (Exception) { /* the next hour */ }
        if (!Directory.Exists(tempRoot)) return;
        var before = UtcNow() - TempLifetime;
        foreach (var entry in Directory.GetFileSystemEntries(tempRoot))
            try { if (Directory.GetLastWriteTimeUtc(entry) < before) Delete(entry); } catch (Exception) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Delay(Every, stop); }
            catch (OperationCanceledException) { return; }
            Sweep();
        }
    }

    private static void Delete(string entry)
    {
        try
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* in use: the next sweep */ }
    }
}
