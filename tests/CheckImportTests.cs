using DmcMcp;
using Xunit;

/**
 * An import nobody waited out: publish_post hands back the importId after 10 minutes of waiting, and before
 * this tool there was nothing to report its result with — even though the server carries on.
 */
public class CheckImportTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    [Fact]
    public async Task ReportsAFinishedImport()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Done(new ImportComponent("Fanuc 0i", "POST_PROCESSOR", "p1", true)));
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", slug: "fanuc-0i");

        var res = await Tools(dmc).CheckImport("imp1");

        Assert.Contains("Draft created: Fanuc 0i", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
        Assert.Empty(dmc.Starts); // re-sends nothing
    }

    [Fact]
    public async Task AStillRunningImportIsReportedWithoutWaitingForever()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Running("post.sppx"));
        var tools = Tools(dmc);
        tools.MaxWait = TimeSpan.Zero;

        var res = await tools.CheckImport("imp1");

        Assert.Contains("still running", res);
        Assert.Contains("imp1", res);
    }

    [Fact]
    public async Task AnUnknownImportIsExplained()
    {
        var dmc = new FakeDmcClient { FailProgress = new DmcHttpException(404, "") };
        var res = await Tools(dmc).CheckImport("old");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("not found", res);
    }

    [Fact]
    public async Task RequiresLogin()
    {
        Assert.Contains("dmc-mcp login", await Tools(new FakeDmcClient(), token: null).CheckImport("imp1"));
    }
}
