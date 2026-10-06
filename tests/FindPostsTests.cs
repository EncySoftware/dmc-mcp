using DmcMcp;
using Xunit;

/**
 * Looking for duplicates before uploading: published ones from the catalogue, your own from /products/my. Both
 * sources in one answer, your own marked, so the agent can say "this one already exists — update it?".
 */
public class FindPostsTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    [Fact]
    public async Task ListsPublishedAndOwnDrafts()
    {
        var dmc = new FakeDmcClient();
        dmc.Published.Add(FakeDmcClient.Post("p1", "Fanuc 0i for Haas VF-2", status: "PUBLISHED", slug: "fanuc-0i-haas"));
        dmc.Mine.Add(FakeDmcClient.Post("d1", "Fanuc 0i-MF draft"));

        var res = await Tools(dmc).FindPosts(query: "Fanuc");

        Assert.Contains("Fanuc 0i for Haas VF-2", res);
        Assert.Contains("published", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i-haas", res);
        Assert.Contains("Fanuc 0i-MF draft", res);
        Assert.Contains("[yours]", res);
        Assert.Equal(("Fanuc", null, null), dmc.Searches[0]);
    }

    /** Your own all come back — filter them here by name, control and machine, as the catalogue does by query. */
    [Fact]
    public async Task FiltersOwnPostsByTheQueryLocally()
    {
        var dmc = new FakeDmcClient();
        dmc.Mine.Add(FakeDmcClient.Post("d1", "Siemens 828D for DMG", controller: "Siemens"));
        dmc.Mine.Add(FakeDmcClient.Post("d2", "Post for Haas", controller: "Fanuc"));

        var res = await Tools(dmc).FindPosts(query: "fanuc");

        Assert.Contains("Post for Haas", res);
        Assert.DoesNotContain("Siemens 828D", res);
    }

    [Fact]
    public async Task NothingFoundSaysSo()
    {
        var res = await Tools(new FakeDmcClient()).FindPosts(controllerManufacturer: "Heidenhain");
        Assert.Contains("found nothing", res);
    }

    [Fact]
    public async Task RequiresAtLeastOneCriterion()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).FindPosts();
        Assert.StartsWith("ERROR", res);
        Assert.Empty(dmc.Searches);
    }

    /** Posts only by default; contentType=ANY opens up schemas, interpreters and kits. */
    [Fact]
    public async Task OtherTypesOnlyWhenAsked()
    {
        var dmc = new FakeDmcClient();
        dmc.Mine.Add(FakeDmcClient.Post("s1", "Haas VF-2 schema", contentType: "MACHINE_SCHEMA"));
        dmc.Mine.Add(FakeDmcClient.Post("d1", "Post for VF-2"));

        var posts = await Tools(dmc).FindPosts(query: "VF-2");
        Assert.DoesNotContain("Haas VF-2 schema", posts);

        var any = await Tools(dmc).FindPosts(query: "VF-2", contentType: "any");
        Assert.Contains("Haas VF-2 schema", any);
        Assert.Contains("MACHINE_SCHEMA", any);
        Assert.Contains("Post for VF-2", any);

        var schemas = await Tools(dmc).FindPosts(query: "VF-2", contentType: "machine_schema");
        Assert.Contains("Haas VF-2 schema", schemas);
        Assert.DoesNotContain("Post for VF-2", schemas);
    }

    [Fact]
    public async Task RejectsAnUnknownContentType()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).FindPosts(query: "x", contentType: "TOOLPATH");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("MACHINE_SCHEMA", res);
        Assert.Empty(dmc.Searches);
    }

    /** Without sign-in the published ones are still searched; for your own drafts, an honest "cannot see them". */
    [Fact]
    public async Task WorksWithoutLoginForPublishedOnly()
    {
        var dmc = new FakeDmcClient();
        dmc.Published.Add(FakeDmcClient.Post("p1", "Fanuc 0i for Haas VF-2", status: "PUBLISHED"));
        dmc.Mine.Add(FakeDmcClient.Post("d1", "Fanuc secret draft"));

        var res = await Tools(dmc, token: null).FindPosts(query: "Fanuc");

        Assert.Contains("Fanuc 0i for Haas VF-2", res);
        Assert.DoesNotContain("Fanuc secret draft", res);
        Assert.Contains("dmc-mcp login", res);
    }
}
