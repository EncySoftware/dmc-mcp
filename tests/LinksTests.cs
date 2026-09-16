using DmcMcp;
using Xunit;

/** Явная связь «пост сделан для станка»: поиск схемы по каталогу и MADE_FOR от поста к схемам. */
public class LinksTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static ProductInfo Schema(string id, string name, string? slug = null) =>
        FakeDmcClient.Post(id, name, status: "PUBLISHED", slug: slug, contentType: "MACHINE_SCHEMA");

    [Fact]
    public async Task SearchSchemasListsMachines()
    {
        var dmc = new FakeDmcClient();
        dmc.Schemas.Add(Schema("s1", "Haas VF-2", "haas-vf-2"));

        var res = await Tools(dmc).SearchSchemas(query: "VF-2");

        Assert.Contains("Haas VF-2", res);
        Assert.Contains("https://dmc.test/product/haas-vf-2", res);
        Assert.Contains("id s1", res);
        Assert.Equal(("VF-2", null, null), dmc.Searches[0]);
    }

    [Fact]
    public async Task SearchSchemasRequiresACriterion()
    {
        var dmc = new FakeDmcClient();
        Assert.StartsWith("ОШИБКА", await Tools(dmc).SearchSchemas());
        Assert.Empty(dmc.Searches);
    }

    [Fact]
    public async Task LinksAPostToTheGivenSchemas()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i");
        dmc.Schemas.Add(Schema("s1", "Haas VF-2"));
        dmc.Schemas.Add(Schema("s2", "Haas VF-4"));

        var res = await Tools(dmc).LinkPostToMachines("p1", "s1, s2");

        var added = Assert.Single(dmc.AddedLinks);
        Assert.Equal("p1", added.Id);
        Assert.Equal("MADE_FOR", added.LinkType);
        Assert.Equal(new[] { "s1", "s2" }, added.Targets);
        Assert.Contains("Fanuc 0i", res);
        Assert.Contains("Haas VF-2", res);
        Assert.Contains("Haas VF-4", res);
    }

    /** MADE_FOR идёт только от поста: схему или кит бэкенд отвергнет — говорим раньше него. */
    [Fact]
    public async Task RefusesToLinkANonPost()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["s1"] = Schema("s1", "Haas VF-2");
        var res = await Tools(dmc).LinkPostToMachines("s1", "s2");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("не пост", res);
        Assert.Empty(dmc.AddedLinks);
    }

    [Fact]
    public async Task RefusesEmptyIds()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i");
        Assert.StartsWith("ОШИБКА", await Tools(dmc).LinkPostToMachines("p1", " , "));
        Assert.Empty(dmc.AddedLinks);
    }
}
