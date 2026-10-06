using System.Text.Json;
using DmcMcp;
using Xunit;

/**
 * A new version of an existing post: the file goes to temporary storage, then a full PUT with its path.
 * Moving it out of tmp/, deleting the old archive and updating the licence container are the backend's job.
 */
public class ReplacePostFileTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dmc-" + Guid.NewGuid().ToString("N")[..8] + ".sppx");
    public ReplacePostFileTests() => File.WriteAllText(_file, "x");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithPost(string status = "DRAFT")
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", status: status, slug: "fanuc-0i");
        return dmc;
    }

    [Fact]
    public async Task UploadsAndPutsTheNewArchivePath()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).ReplacePostFile("p1", _file);

        Assert.Equal(Path.GetFileName(_file), Assert.Single(dmc.Uploads));
        var (id, body) = Assert.Single(dmc.Puts);
        Assert.Equal("p1", id);
        Assert.Equal("tmp/u1/" + Path.GetFileName(_file), body["productFile"]);
        // The rest of the card goes as it was — the PUT is a full one.
        Assert.Equal("Fanuc 0i", ((JsonElement)body["name"]!).GetString());
        Assert.Contains("replaced", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
    }

    /** A published post's new archive goes live without re-moderation — say so. */
    [Fact]
    public async Task APublishedPostIsToldTheArchiveGoesLiveAtOnce()
    {
        var res = await Tools(WithPost("PUBLISHED")).ReplacePostFile("p1", _file);
        Assert.Contains("at once", res);
    }

    [Fact]
    public async Task RefusesAWrongFileTypeBeforeUploading()
    {
        var dmc = WithPost();
        var txt = Path.ChangeExtension(_file, ".txt");
        File.WriteAllText(txt, "x");
        try { Assert.Contains("not a post", await Tools(dmc).ReplacePostFile("p1", txt)); }
        finally { File.Delete(txt); }
        Assert.Empty(dmc.Uploads);
    }

    /** Read the card BEFORE uploading: someone else's or a nonexistent id must not cost a needless file in tmp/. */
    [Fact]
    public async Task UnknownPostIsAnErrorAndNothingIsUploaded()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).ReplacePostFile("nope", _file);
        Assert.Contains("did not find", res);
        Assert.Empty(dmc.Uploads);
    }

    [Fact]
    public async Task RefusesWithoutLogin()
    {
        var dmc = WithPost();
        var res = await Tools(dmc, token: null).ReplacePostFile("p1", _file);
        Assert.Contains("dmc-mcp login", res);
        Assert.Empty(dmc.Uploads);
    }
}
