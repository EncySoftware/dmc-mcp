using DmcMcp;
using Xunit;

/**
 * Импорт, который не дождались: publish_post отдаёт importId через 10 минут ожидания, и до этого
 * инструмента доложить его результат было нечем — сервер-то продолжает.
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

        Assert.Contains("Создан черновик: Fanuc 0i", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
        Assert.Empty(dmc.Starts); // ничего не отправляет заново
    }

    [Fact]
    public async Task AStillRunningImportIsReportedWithoutWaitingForever()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Running("post.sppx"));
        var tools = Tools(dmc);
        tools.MaxWait = TimeSpan.Zero;

        var res = await tools.CheckImport("imp1");

        Assert.Contains("всё ещё идёт", res);
        Assert.Contains("imp1", res);
    }

    [Fact]
    public async Task AnUnknownImportIsExplained()
    {
        var dmc = new FakeDmcClient { FailProgress = new DmcHttpException(404, "") };
        var res = await Tools(dmc).CheckImport("old");
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("не найден", res);
    }

    [Fact]
    public async Task RequiresLogin()
    {
        Assert.Contains("dmc-mcp login", await Tools(new FakeDmcClient(), token: null).CheckImport("imp1"));
    }
}
