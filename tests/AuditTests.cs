using DmcMcp;
using Xunit;

/**
 * После пачки: каким черновикам чего не хватает по правилам модерации (имя, производитель станка,
 * тип станка, архив), и отправить готовые одним вызовом.
 */
public class AuditTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithDrafts()
    {
        var dmc = new FakeDmcClient();
        var ready = FakeDmcClient.Post("d1", "Fanuc 0i", slug: "fanuc-0i");
        var bare = FakeDmcClient.Post("d2", "", machineMaker: null, machineType: null, withArchive: false);
        var published = FakeDmcClient.Post("p3", "Old one", status: "PUBLISHED");
        foreach (var p in new[] { ready, bare, published }) { dmc.Mine.Add(p); dmc.Products[p.Id] = p; }
        return dmc;
    }

    [Fact]
    public async Task AuditListsWhatEachDraftLacks()
    {
        var res = await Tools(WithDrafts()).AuditDrafts();

        Assert.Contains("Fanuc 0i", res);
        Assert.Contains("готов", res);
        Assert.Contains("без обложки", res);
        Assert.Contains("d2", res);
        Assert.Contains("производитель станка", res);
        Assert.Contains("тип станка", res);
        Assert.Contains("архив", res);
        Assert.DoesNotContain("Old one", res); // опубликованное — не черновик
        Assert.Contains("Готовы: 1", res);
    }

    [Fact]
    public async Task SubmitDraftsSendsTheReadyOnesAndExplainsTheRest()
    {
        var dmc = WithDrafts();
        var res = await Tools(dmc).SubmitDrafts("d1, d2");

        Assert.Equal(("d1", "PENDING_REVIEW"), Assert.Single(dmc.StatusCalls));
        Assert.Contains("Fanuc 0i", res);
        Assert.Contains("на модерацию", res);
        Assert.Contains("d2", res);
        Assert.Contains("не хватает", res);
    }

    [Fact]
    public async Task SubmitDraftsAllTakesEveryReadyDraft()
    {
        var dmc = WithDrafts();
        var res = await Tools(dmc).SubmitDrafts("ALL");
        Assert.Equal(("d1", "PENDING_REVIEW"), Assert.Single(dmc.StatusCalls));
        Assert.Contains("d2", res);
    }

    [Fact]
    public async Task NothingToSubmitSaysSo()
    {
        var dmc = new FakeDmcClient();
        Assert.Contains("нет черновиков", await Tools(dmc).SubmitDrafts("ALL"));
        Assert.StartsWith("ОШИБКА", await Tools(dmc).SubmitDrafts(" , "));
    }

    [Fact]
    public async Task AuditNeedsLogin()
    {
        Assert.Contains("dmc-mcp login", await Tools(WithDrafts(), token: null).AuditDrafts());
    }
}
