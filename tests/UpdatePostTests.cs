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
}
