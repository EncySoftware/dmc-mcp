using System.Text.Json;
using DmcMcp;
using Xunit;

public class UpdatePostTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithPost()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", slug: "fanuc-0i");
        return dmc;
    }

    /** PUT у DMC полный: нетронутые поля (архив!) едут как были, лишние поля карточки — нет. */
    [Fact]
    public async Task PutsTheWholeRowWithOnlyTheGivenFieldsChanged()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).UpdatePost("p1", controllerManufacturer: "Siemens", numberOfAxes: 5);

        var (id, body) = Assert.Single(dmc.Puts);
        Assert.Equal("p1", id);
        Assert.Equal("Siemens", body["controllerManufacturer"]);
        Assert.Equal(5, body["numberOfAxes"]);
        Assert.Equal("products/p1/post.zip", ((JsonElement)body["productFile"]!).GetString());
        Assert.Equal("Haas", ((JsonElement)body["machineManufacturer"]!).GetString());
        Assert.False(body.ContainsKey("id"));
        Assert.False(body.ContainsKey("downloadCount"));
        Assert.Contains("Fanuc → Siemens", res);
        Assert.Contains("3 → 5", res);
    }

    [Fact]
    public async Task RejectsAnUnknownMachineTypeBeforeWriting()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).UpdatePost("p1", machineType: "FRAISEUSE");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("MILLING", res);
        Assert.Empty(dmc.Puts);
    }

    [Fact]
    public async Task NormalisesMachineTypeCase()
    {
        var dmc = WithPost();
        await Tools(dmc).UpdatePost("p1", machineType: "turning");
        Assert.Equal("TURNING", dmc.Puts[0].Body["machineType"]);
    }

    [Fact]
    public async Task NothingToChangeDoesNotWrite()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).UpdatePost("p1", name: "Fanuc 0i");
        Assert.Contains("Менять нечего", res);
        Assert.Empty(dmc.Puts);
    }

    [Fact]
    public async Task UnknownPostIsAnError()
    {
        var res = await Tools(new FakeDmcClient()).UpdatePost("nope", name: "x");
        Assert.Contains("не нашёл", res);
    }

    /** A schema card with the work area the archive gave it: X 508 × Y 406.4 × Z 508. */
    private static FakeDmcClient WithSchema()
    {
        var dmc = new FakeDmcClient();
        var raw = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = "s1", ["slug"] = "haas-vf-2", ["name"] = "Haas VF-2", ["contentType"] = "MACHINE_SCHEMA",
            ["category"] = "CNC_MACHINES", ["publicationStatus"] = "PENDING_REVIEW",
            ["productFile"] = "products/s1/vf2.zip", ["machineManufacturer"] = "Haas", ["machineType"] = "MILLING",
            ["numberOfAxes"] = 3, ["travelXMm"] = 508.0, ["travelYMm"] = 406.4, ["travelZMm"] = 508.0,
        });
        dmc.Products["s1"] = DmcJson.Product(raw);
        return dmc;
    }

    /**
     * Yuriy, 2026-10-02: after checking 85 cards against the archives, the axis travels of ten were
     * off and nothing in the tool could set them. They go in like every other field — only the
     * given one changes, the other two ride along as they were.
     */
    [Fact]
    public async Task SetsAnAxisTravelAndKeepsTheOthers()
    {
        var dmc = WithSchema();
        var res = await Tools(dmc).UpdatePost("s1", travelYMm: 457.2);

        var (_, body) = Assert.Single(dmc.Puts);
        Assert.Equal(457.2, body["travelYMm"]);
        Assert.Equal(508.0, ((JsonElement)body["travelXMm"]!).GetDouble());
        Assert.Equal(508.0, ((JsonElement)body["travelZMm"]!).GetDouble());
        Assert.Contains("travelYMm: 406.4 → 457.2", res);
    }

    [Fact]
    public async Task ATravelTheCardAlreadyHasIsNotAChange()
    {
        var dmc = WithSchema();
        var res = await Tools(dmc).UpdatePost("s1", travelXMm: 508);
        Assert.Contains("Менять нечего", res);
        Assert.Empty(dmc.Puts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(200000)]
    public async Task RejectsAnImpossibleTravelBeforeWriting(double travel)
    {
        var dmc = WithSchema();
        var res = await Tools(dmc).UpdatePost("s1", travelZMm: travel);
        Assert.StartsWith("ОШИБКА", res);
        Assert.Empty(dmc.Puts);
    }
}
