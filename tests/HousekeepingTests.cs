using DmcMcp;
using Xunit;

/** Housekeeping: what I have and in which status; removing a mistaken draft — and only a draft. */
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
        Assert.Contains("draft: 1", res);
        Assert.Contains("published: 1", res);
    }

    [Fact]
    public async Task FiltersByStatusCaseInsensitively()
    {
        var res = await Tools(WithMine()).ListMyPosts(status: "draft");
        Assert.Contains("Draft A", res);
        Assert.DoesNotContain("Published B", res);
    }

    /** Posts by default; contentType=ANY lists all your own with their type, a given type lists only that type. */
    [Fact]
    public async Task ListsOtherTypesOnlyWhenAsked()
    {
        var any = await Tools(WithMine()).ListMyPosts(contentType: "ANY");
        Assert.Contains("Some schema", any);
        Assert.Contains("MACHINE_SCHEMA", any);
        Assert.Contains("Draft A", any);

        var schemas = await Tools(WithMine()).ListMyPosts(contentType: "machine_schema");
        Assert.Contains("Some schema", schemas);
        Assert.DoesNotContain("Draft A", schemas);

        Assert.StartsWith("ERROR", await Tools(WithMine()).ListMyPosts(contentType: "TOOLPATH"));
    }

    [Fact]
    public async Task RejectsAnUnknownStatus()
    {
        var res = await Tools(WithMine()).ListMyPosts(status: "weird");
        Assert.StartsWith("ERROR", res);
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
        Assert.Contains("deleted", res);
    }

    /** The tool does not delete a published post — that is unpublishing, done deliberately in the DMC account. */
    [Fact]
    public async Task RefusesToDeleteAPublishedPost()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p2"] = FakeDmcClient.Post("p2", "Published B", status: "PUBLISHED");
        var res = await Tools(dmc).DeletePost("p2");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("is published", res);
        Assert.Empty(dmc.Deleted);
    }

    [Fact]
    public async Task ALicensedPostShowsTheBackendReason()
    {
        var dmc = new FakeDmcClient { FailDelete = new DmcHttpException(409, """{"error":"Cannot delete product with active licenses"}""") };
        dmc.Products["d1"] = FakeDmcClient.Post("d1", "Draft A");
        var res = await Tools(dmc).DeletePost("d1");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("active licenses", res);
    }

    [Fact]
    public async Task DeleteUnknownIsAnError()
    {
        Assert.Contains("did not find", await Tools(new FakeDmcClient()).DeletePost("nope"));
    }
}
