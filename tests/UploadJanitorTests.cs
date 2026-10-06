using DmcMcp;
using Xunit;

/**
 * The hosted server's disk is the DMC VPS's, shared with the backend and Postgres: expired uploads go every hour
 * even when nobody uploads, and temp folders a killed call left behind do not stay.
 */
public class UploadJanitorTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "dmc-jn-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_data, true); } catch { } }

    private string Uploads => Path.Combine(_data, "uploads");
    private string Tmp => Path.Combine(_data, "tmp");

    private string TempFolder(string name, TimeSpan age)
    {
        var dir = Path.Combine(Tmp, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "post.sppx"), "x");
        Directory.SetLastWriteTimeUtc(dir, _now.UtcDateTime - age); // nothing added or removed since
        return dir;
    }

    [Fact]
    public async Task SweepPurgesExpiredUploadsAndOldTempFolders()
    {
        var store = new UploadStore(Uploads, () => _now);
        var saved = await store.Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        _now += TimeSpan.FromHours(25);
        var old = TempFolder("old", TimeSpan.FromHours(25));
        var fresh = TempFolder("fresh", TimeSpan.FromMinutes(5));

        new UploadJanitor(store, Tmp) { UtcNow = () => _now.UtcDateTime }.Sweep();

        Assert.Null(store.Resolve(saved.Ref));
        Assert.Empty(Directory.GetDirectories(Uploads));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh)); // a call may still be using it
    }

    /** Hourly, not only from the next upload's Save. */
    [Fact]
    public async Task ItSweepsEveryHour()
    {
        var store = new UploadStore(Uploads, () => _now);
        await store.Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        _now += TimeSpan.FromHours(25);
        var delays = new List<TimeSpan>();
        var janitor = new UploadJanitor(store, Tmp)
        {
            UtcNow = () => _now.UtcDateTime,
            Delay = (t, _) =>
            {
                delays.Add(t);
                return delays.Count == 2 ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask;
            },
        };
        await janitor.StartAsync(CancellationToken.None);
        await janitor.ExecuteTask!;
        Assert.Equal(new[] { UploadJanitor.Every, UploadJanitor.Every }, delays);
        Assert.Empty(Directory.GetDirectories(Uploads));
    }

    /** At startup nothing in tmp belongs to a live call: a container stopped mid-download left it. */
    [Fact]
    public void StartupEmptiesTmp()
    {
        TempFolder("left-by-a-killed-call", TimeSpan.FromMinutes(1));
        UploadJanitor.ClearTemp(Tmp);
        Assert.Empty(Directory.GetFileSystemEntries(Tmp));
    }
}
