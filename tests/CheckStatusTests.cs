using DmcMcp;
using Xunit;

public class CheckStatusTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    [Fact]
    public async Task TellsTheStatusInWordsWithALink()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", status: "PENDING_REVIEW", slug: "fanuc-0i");
        var res = await Tools(dmc).CheckPostStatus("p1");

        Assert.Contains("pending moderation", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
        Assert.Contains("Fanuc", res);
        Assert.Contains("Haas", res);
    }

    /** DMC's 404 means both "no such thing" and "someone else's draft" — say so, and hint at signing in if needed. */
    [Fact]
    public async Task UnknownPostIsExplainedNotThrown()
    {
        var res = await Tools(new FakeDmcClient(), token: null).CheckPostStatus("nope");
        Assert.Contains("did not find", res);
        Assert.Contains("dmc-mcp login", res);
    }

    [Fact]
    public void StatusWordsCoverEveryBackendValue()
    {
        foreach (var s in new[] { "DRAFT", "PENDING_REVIEW", "PUBLISHED", "REJECTED", "DISABLED", "ARCHIVED" })
            Assert.NotEqual(s, DmcTools.StatusWord(s));
    }

    [Fact]
    public void HttpErrorsBecomeAdvice()
    {
        Assert.Contains("dmc-mcp login", DmcTools.Explain(new DmcHttpException(401, "")));
        Assert.Contains("publisher", DmcTools.Explain(new DmcHttpException(403, "")));
        Assert.Contains("an import running", DmcTools.Explain(new DmcHttpException(409, """{"error":"An import is already running"}""")));
        Assert.Contains("Missing required fields: Machine Type",
            DmcTools.Explain(new DmcHttpException(400, """{"error":"Missing required fields: Machine Type"}""")));
    }
}
