using DmcMcp;
using Xunit;

public class SubmitPostTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithPost(string status = "DRAFT")
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", status: status, slug: "fanuc-0i");
        return dmc;
    }

    [Fact]
    public async Task SendsTheDraftToReview()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).SubmitPost("p1");
        Assert.Equal(("p1", "PENDING_REVIEW"), Assert.Single(dmc.StatusCalls));
        Assert.Contains("на модерацию", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
    }

    /** Бэкенд перечисляет недостающее — автор видит список и что делать, а не «400». */
    [Fact]
    public async Task MissingFieldsAreListedWithTheFix()
    {
        var dmc = WithPost();
        dmc.FailStatus = new DmcHttpException(400, """{"error":"Missing required fields: Machine Type, Product File"}""");
        var res = await Tools(dmc).SubmitPost("p1");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("Machine Type, Product File", res);
        Assert.Contains("update_post", res);
    }

    [Fact]
    public async Task AlreadySubmittedIsLeftAlone()
    {
        var dmc = WithPost("PENDING_REVIEW");
        var res = await Tools(dmc).SubmitPost("p1");
        Assert.Contains("уже на модерации", res);
        Assert.Empty(dmc.StatusCalls);
    }

    [Fact]
    public async Task RefusesWithoutLogin()
    {
        var dmc = WithPost();
        var res = await Tools(dmc, token: null).SubmitPost("p1");
        Assert.Contains("dmc-mcp login", res);
        Assert.Empty(dmc.StatusCalls);
    }
}
