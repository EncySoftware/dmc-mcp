using System.Text.Json;
using DmcMcp;
using Xunit;

/**
 * Your own cover — from the author, or from the agent if it has something to draw with: the file goes to storage,
 * the tmp/… path onto the card with a full PUT. The server AI is not involved.
 */
public class SetCoverTests : IDisposable
{
    private readonly string _png = Path.Combine(Path.GetTempPath(), "dmc-" + Guid.NewGuid().ToString("N")[..8] + ".png");
    public SetCoverTests() => File.WriteAllBytes(_png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    public void Dispose() { try { File.Delete(_png); } catch { } }

    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithPost()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", slug: "fanuc-0i");
        return dmc;
    }

    [Fact]
    public async Task UploadsThePictureAndPutsItAsTheCover()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).SetCover("p1", _png);

        Assert.Equal(Path.GetFileName(_png), Assert.Single(dmc.Uploads));
        var (id, body) = Assert.Single(dmc.Puts);
        Assert.Equal("p1", id);
        Assert.Equal("tmp/u1/" + Path.GetFileName(_png), body["imageUrl"]);
        Assert.Equal("Fanuc 0i", ((JsonElement)body["name"]!).GetString()); // the rest as it was
        Assert.Contains("Cover", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
        Assert.Empty(dmc.AiCalls);
    }

    [Fact]
    public async Task RefusesANonImage()
    {
        var dmc = WithPost();
        var txt = Path.ChangeExtension(_png, ".txt");
        File.WriteAllText(txt, "x");
        try { Assert.Contains("not a picture", await Tools(dmc).SetCover("p1", txt)); }
        finally { File.Delete(txt); }
        Assert.Empty(dmc.Uploads);
    }

    [Fact]
    public async Task UnknownPostUploadsNothing()
    {
        var dmc = new FakeDmcClient();
        Assert.Contains("did not find", await Tools(dmc).SetCover("nope", _png));
        Assert.Empty(dmc.Uploads);
    }

    [Fact]
    public async Task RefusesWithoutLogin()
    {
        var dmc = WithPost();
        Assert.Contains("dmc-mcp login", await Tools(dmc, token: null).SetCover("p1", _png));
        Assert.Empty(dmc.Uploads);
    }
}
