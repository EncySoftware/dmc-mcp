using DmcMcp;
using Xunit;

/**
 * Поиск дублей до заливки: опубликованное — из каталога, своё — из /products/my. Оба источника
 * в одном ответе, свои помечены, чтобы агент мог сказать «такой уже есть — обновить?».
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
        Assert.Contains("опубликован", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i-haas", res);
        Assert.Contains("Fanuc 0i-MF draft", res);
        Assert.Contains("ваш", res);
        Assert.Equal(("Fanuc", null, null), dmc.Searches[0]);
    }

    /** Свои приходят все — фильтруем на месте по имени, стойке и станку, как каталог по запросу. */
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
        Assert.Contains("ничего не нашёл", res);
    }

    [Fact]
    public async Task RequiresAtLeastOneCriterion()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).FindPosts();
        Assert.StartsWith("ОШИБКА", res);
        Assert.Empty(dmc.Searches);
    }

    /** По умолчанию — только посты; contentType=ANY открывает схемы, интерпретаторы и киты. */
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
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("MACHINE_SCHEMA", res);
        Assert.Empty(dmc.Searches);
    }

    /** Без входа опубликованное всё равно ищется; про свои черновики — честное «не видно». */
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
