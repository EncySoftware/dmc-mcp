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

    /**
     * DMC's product form lists the gallery and calls its first picture the cover. set_cover used to change only
     * imageUrl, so the form showed an old picture as the cover and not the new one (a Fanuc logo, 8 October). The new
     * cover leads the gallery now; the one it replaces stays in it, then the rest, each once.
     */
    [Fact]
    public async Task TheNewCoverLeadsTheGalleryAndTheOldOneStaysInIt()
    {
        var dmc = new FakeDmcClient();
        var raw = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = "p1", ["slug"] = "fanuc-0i", ["name"] = "Fanuc 0i", ["contentType"] = "INTERPRETER",
            ["category"] = "CNC_MACHINES", ["publicationStatus"] = "PUBLISHED", ["productFile"] = "products/p1/i.zip",
            ["imageUrl"] = "products/p1/cover/old.png",
            ["images"] = new[] { "products/p1/cover/side.png", "products/p1/cover/old.png" },
        });
        dmc.Products["p1"] = DmcJson.Product(raw);

        await Tools(dmc).SetCover("p1", _png);

        var (_, body) = Assert.Single(dmc.Puts);
        var cover = "tmp/u1/" + Path.GetFileName(_png);
        Assert.Equal(cover, body["imageUrl"]);
        Assert.Equal(new[] { cover, "products/p1/cover/old.png", "products/p1/cover/side.png" },
            Assert.IsAssignableFrom<IEnumerable<string>>(body["images"]).ToArray());
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
