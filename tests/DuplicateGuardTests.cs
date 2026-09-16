using DmcMcp;
using Xunit;

/**
 * Защита от дублей и сухой прогон. В каталоге 165 опубликованных постов делят 70 имён — значит
 * инструмент должен искать похожее ДО загрузки и останавливаться, пока автор не скажет force.
 */
public class DuplicateGuardTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dmc-" + Guid.NewGuid().ToString("N")[..8] + ".sppx");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-dup-" + Guid.NewGuid().ToString("N")[..8]);

    public DuplicateGuardTests()
    {
        File.WriteAllText(_file, "x");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.sppx"), "x");
        File.WriteAllText(Path.Combine(_dir, "b.sppx"), "y");
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient Ready(params ImportComponent[] comps)
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Done(comps));
        foreach (var c in comps) dmc.Products[c.ProductId] = FakeDmcClient.Post(c.ProductId, c.Name);
        return dmc;
    }

    [Fact]
    public async Task PublishPostStopsWhenASimilarPostExists()
    {
        var dmc = Ready(new ImportComponent("Fanuc 0i", "POST_PROCESSOR", "p1", true));
        dmc.Published.Add(FakeDmcClient.Post("p9", "Fanuc 0i for Haas VF-2", status: "PUBLISHED", slug: "fanuc-0i-haas"));

        var res = await Tools(dmc).PublishPost(_file, name: "Fanuc 0i");

        Assert.StartsWith("Похожие уже есть", res);
        Assert.Contains("Fanuc 0i for Haas VF-2", res);
        Assert.Contains("force", res);
        Assert.Contains("replace_post_file", res);
        Assert.Empty(dmc.Starts);
        Assert.Equal("Fanuc 0i", dmc.Searches[0].Query);
    }

    [Fact]
    public async Task PublishPostProceedsWithForce()
    {
        var dmc = Ready(new ImportComponent("Fanuc 0i", "POST_PROCESSOR", "p1", true));
        dmc.Published.Add(FakeDmcClient.Post("p9", "Fanuc 0i for Haas VF-2", status: "PUBLISHED"));

        var res = await Tools(dmc).PublishPost(_file, name: "Fanuc 0i", force: true);

        Assert.Single(dmc.Starts);
        Assert.Contains("Создан черновик", res);
    }

    /** Без подсказки имени ищем по имени файла — иначе защита молчала бы ровно тогда, когда автор ленится. */
    [Fact]
    public async Task PublishPostSearchesByTheFileNameWhenThereIsNoHint()
    {
        var dmc = Ready(new ImportComponent("x", "POST_PROCESSOR", "p1", false));
        await Tools(dmc).PublishPost(_file);
        Assert.Equal(Path.GetFileNameWithoutExtension(_file), dmc.Searches[0].Query);
    }

    [Fact]
    public async Task PublishFolderDryRunListsThePlanAndUploadsNothing()
    {
        var dmc = new FakeDmcClient();
        var manifest = Path.Combine(_dir, "manifest.csv");
        File.WriteAllText(manifest, "file,name,controllerManufacturer\nb.sppx,Fanuc 0i-MF,Siemens\n");

        var res = await Tools(dmc).PublishFolder(_dir, manifest, dryRun: true);

        Assert.Contains("Сухой прогон", res);
        Assert.Contains("a ← a.sppx", res);
        Assert.Contains("Fanuc 0i-MF ← b.sppx", res);
        Assert.Contains("controllerManufacturer=Siemens", res);
        Assert.Empty(dmc.Starts);
    }

    [Fact]
    public async Task PublishFolderStopsOnDuplicatesUnlessForced()
    {
        var dmc = Ready(new ImportComponent("a", "POST_PROCESSOR", "p1", false), new ImportComponent("b", "POST_PROCESSOR", "p2", false));
        dmc.Published.Add(FakeDmcClient.Post("p9", "a", status: "PUBLISHED"));

        var stopped = await Tools(dmc).PublishFolder(_dir);
        Assert.StartsWith("Похожие уже есть", stopped);
        Assert.Empty(dmc.Starts);

        var forced = await Tools(dmc).PublishFolder(_dir, force: true);
        Assert.Single(dmc.Starts);
        Assert.Contains("2 черновик", forced);
    }

    [Fact]
    public async Task PublishFolderForwardsTheHints()
    {
        var dmc = Ready(new ImportComponent("a", "POST_PROCESSOR", "p1", false), new ImportComponent("b", "POST_PROCESSOR", "p2", false));
        await Tools(dmc).PublishFolder(_dir, nameHint: "M3X = 3-axis mill", descriptionHint: "for Haas", force: true);
        var s = Assert.Single(dmc.Starts);
        Assert.Equal("M3X = 3-axis mill", s.Name);
        Assert.Equal("for Haas", s.Desc);
    }
}
