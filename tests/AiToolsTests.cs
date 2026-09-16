using DmcMcp;
using Xunit;

/**
 * ИИ по запросу. Бэкенд ничего не сохраняет сам: описание — текст, обложка и файлы — пути tmp/…,
 * поэтому каждый инструмент заканчивается полным PUT с новым значением поверх текущей карточки.
 */
public class AiToolsTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithPost()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", slug: "fanuc-0i");
        return dmc;
    }

    [Fact]
    public async Task GeneratesAndSavesADescription()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).GenerateDescription("p1");
        Assert.Equal(new[] { "description" }, dmc.AiCalls);
        var (id, body) = Assert.Single(dmc.Puts);
        Assert.Equal("p1", id);
        Assert.Equal("Generated description", body["description"]);
        Assert.Contains("Generated description", res);
        Assert.Contains("сохранено", res);
    }

    [Fact]
    public async Task GenerateDescriptionCanSkipSaving()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).GenerateDescription("p1", save: false);
        Assert.Empty(dmc.Puts);
        Assert.Contains("Generated description", res);
    }

    [Fact]
    public async Task CoverFromTheArchiveIsTheDefault()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).RegenerateCover("p1");
        Assert.Equal(new[] { "archive-preview" }, dmc.AiCalls);
        Assert.Equal("tmp/u9/preview.png", Assert.Single(dmc.Puts).Body["imageUrl"]);
        Assert.Contains("архив", res);
    }

    [Fact]
    public async Task AnArchiveWithoutAPictureAdvisesAi()
    {
        var dmc = WithPost();
        dmc.ArchiveImage = null;
        var res = await Tools(dmc).RegenerateCover("p1");
        Assert.Contains("нет картинки", res);
        Assert.Contains("ai", res);
        Assert.Empty(dmc.Puts);
    }

    [Fact]
    public async Task CoverFromAi()
    {
        var dmc = WithPost();
        await Tools(dmc).RegenerateCover("p1", source: "ai");
        Assert.Equal(new[] { "image" }, dmc.AiCalls);
        Assert.Equal("tmp/u9/ai-cover.png", Assert.Single(dmc.Puts).Body["imageUrl"]);
    }

    [Fact]
    public async Task RejectsAnUnknownCoverSource()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).RegenerateCover("p1", source: "logo");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("archive", res);
        Assert.Empty(dmc.AiCalls);
    }

    [Fact]
    public async Task SampleCodeAndCodesListAreAttached()
    {
        var dmc = WithPost();
        await Tools(dmc).GenerateSampleCode("p1");
        await Tools(dmc).GenerateCodesList("p1");
        Assert.Equal(new[] { "sample-code", "codes-list" }, dmc.AiCalls);
        Assert.Equal(2, dmc.Puts.Count);
        Assert.Equal("tmp/u9/sample.nc", dmc.Puts[0].Body["sampleOutputCodeFile"]);
        Assert.Equal("tmp/u9/codes.txt", dmc.Puts[1].Body["supportedCodesFile"]);
    }

    [Fact]
    public async Task AiToolsNeedLogin()
    {
        var dmc = WithPost();
        Assert.Contains("dmc-mcp login", await Tools(dmc, token: null).GenerateDescription("p1"));
        Assert.Empty(dmc.AiCalls);
    }
}
