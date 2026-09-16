using DmcMcp;
using Xunit;

/** Хозяйство: что у меня лежит и в каком статусе; убрать ошибочный черновик — и только черновик. */
public class HousekeepingTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithMine()
    {
        var dmc = new FakeDmcClient();
        dmc.Mine.Add(FakeDmcClient.Post("d1", "Draft A"));
        dmc.Mine.Add(FakeDmcClient.Post("p2", "Published B", status: "PUBLISHED", slug: "published-b"));
        dmc.Mine.Add(FakeDmcClient.Post("s1", "Some schema", contentType: "MACHINE_SCHEMA"));
        return dmc;
    }

    [Fact]
    public async Task ListsOwnPostsAndCountsByStatus()
    {
        var res = await Tools(WithMine()).ListMyPosts();
        Assert.Contains("Draft A", res);
        Assert.Contains("Published B", res);
        Assert.DoesNotContain("Some schema", res);
        Assert.Contains("черновик: 1", res);
        Assert.Contains("опубликован: 1", res);
    }

    [Fact]
    public async Task FiltersByStatusCaseInsensitively()
    {
        var res = await Tools(WithMine()).ListMyPosts(status: "draft");
        Assert.Contains("Draft A", res);
        Assert.DoesNotContain("Published B", res);
    }

    [Fact]
    public async Task RejectsAnUnknownStatus()
    {
        var res = await Tools(WithMine()).ListMyPosts(status: "weird");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("DRAFT", res);
    }

    [Fact]
    public async Task ListNeedsLogin()
    {
        Assert.Contains("dmc-mcp login", await Tools(WithMine(), token: null).ListMyPosts());
    }

    [Fact]
    public async Task DeletesADraft()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["d1"] = FakeDmcClient.Post("d1", "Draft A");
        var res = await Tools(dmc).DeletePost("d1");
        Assert.Equal("d1", Assert.Single(dmc.Deleted));
        Assert.Contains("удалён", res);
    }

    /** Опубликованное не удаляем инструментом — это снятие с публикации, оно делается в кабинете осознанно. */
    [Fact]
    public async Task RefusesToDeleteAPublishedPost()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p2"] = FakeDmcClient.Post("p2", "Published B", status: "PUBLISHED");
        var res = await Tools(dmc).DeletePost("p2");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("опубликован", res);
        Assert.Empty(dmc.Deleted);
    }

    [Fact]
    public async Task ALicensedPostShowsTheBackendReason()
    {
        var dmc = new FakeDmcClient { FailDelete = new DmcHttpException(409, """{"error":"Cannot delete product with active licenses"}""") };
        dmc.Products["d1"] = FakeDmcClient.Post("d1", "Draft A");
        var res = await Tools(dmc).DeletePost("d1");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("active licenses", res);
    }

    [Fact]
    public async Task DeleteUnknownIsAnError()
    {
        Assert.Contains("не нашёл", await Tools(new FakeDmcClient()).DeletePost("nope"));
    }
}
