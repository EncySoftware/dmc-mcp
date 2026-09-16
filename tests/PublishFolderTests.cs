using DmcMcp;
using Xunit;

/** Manifest — CSV автора: точные имя и поля вместо догадок ИИ. Только колонка file обязательна. */
public class ManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-man-" + Guid.NewGuid().ToString("N")[..8]);
    public ManifestTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Csv(string text)
    {
        var p = Path.Combine(_dir, "manifest.csv");
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void ParsesQuotedCommasAndOptionalColumns()
    {
        var m = Manifest.Parse(Csv("file,name,controllerManufacturer,machineType,numberOfAxes\n"
                                 + "b.sppx,\"Fanuc 0i-MF, Haas\",Fanuc,milling,3\n"
                                 + "a.sppx,,,,\n"));
        var b = m.For("B.SPPX");
        Assert.NotNull(b);
        Assert.Equal("Fanuc 0i-MF, Haas", b!.Name);
        Assert.Equal("Fanuc", b.Fields.ControllerManufacturer);
        Assert.Equal("milling", b.Fields.MachineType);
        Assert.Equal(3, b.Fields.NumberOfAxes);
        Assert.Null(b.Fields.MachineManufacturer);
        var a = m.For("a.sppx");
        Assert.NotNull(a);
        Assert.Null(a!.Name);
        Assert.True(a.Fields.IsEmpty);
        Assert.Equal(new[] { "a.sppx", "b.sppx" }, m.Files.OrderBy(f => f).ToArray());
    }

    [Fact]
    public void RequiresTheFileColumn()
    {
        var e = Assert.Throws<InvalidDataException>(() => Manifest.Parse(Csv("name,controllerManufacturer\nx,y\n")));
        Assert.Contains("file", e.Message);
    }

    [Fact]
    public void RejectsAnUnknownColumn()
    {
        var e = Assert.Throws<InvalidDataException>(() => Manifest.Parse(Csv("file,colour\na.sppx,red\n")));
        Assert.Contains("colour", e.Message);
    }
}

/**
 * Пачка: один zip с папкой на пост — так bulk-zip делит его на отдельные черновики и берёт имя
 * из имени папки. После импорта поля из manifest накатываются PUT-ом на каждый черновик.
 */
public class PublishFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-folder-" + Guid.NewGuid().ToString("N")[..8]);

    public PublishFolderTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.sppx"), "x");
        File.WriteAllText(Path.Combine(_dir, "b.sppx"), "y");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a post");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private string Manifest(string text)
    {
        var p = Path.Combine(_dir, "manifest.csv");
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public async Task ZipsOneFolderPerPostAndAppliesTheManifest()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Done(
            new ImportComponent("a", "POST_PROCESSOR", "p1", AiEnriched: true),
            new ImportComponent("Fanuc 0i-MF", "POST_PROCESSOR", "p2", AiEnriched: true)));
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "a");
        dmc.Products["p2"] = FakeDmcClient.Post("p2", "Fanuc 0i-MF", controller: "Fanuc");
        var manifest = Manifest("file,name,controllerManufacturer\nb.sppx,Fanuc 0i-MF,Siemens\n");

        var res = await Tools(dmc).PublishFolder(_dir, manifest);

        var s = Assert.Single(dmc.Starts);
        Assert.EndsWith(".zip", s.File);
        Assert.False(File.Exists(s.File)); // временный zip прибран
        Assert.Equal(new[] { "Fanuc 0i-MF/b.sppx", "a/a.sppx" },
            dmc.ZipEntries.OrderBy(e => e, StringComparer.Ordinal).ToArray());
        Assert.Contains("Создан черновик: a", res);
        Assert.Contains("Создан черновик: Fanuc 0i-MF", res);
        var put = Assert.Single(dmc.Puts);
        Assert.Equal("p2", put.Id);
        Assert.Equal("Siemens", put.Body["controllerManufacturer"]);
        Assert.Contains("controllerManufacturer: Fanuc → Siemens", res);
        Assert.Contains("submit_post", res);
    }

    [Fact]
    public async Task WorksWithoutAManifest()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Done(
            new ImportComponent("a", "POST_PROCESSOR", "p1", false),
            new ImportComponent("b", "POST_PROCESSOR", "p2", false)));
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "a");
        dmc.Products["p2"] = FakeDmcClient.Post("p2", "b");

        var res = await Tools(dmc).PublishFolder(_dir);

        Assert.Equal(new[] { "a/a.sppx", "b/b.sppx" }, dmc.ZipEntries.OrderBy(e => e).ToArray());
        Assert.Empty(dmc.Puts);
        Assert.Contains("2 черновик", res);
    }

    [Fact]
    public async Task AFolderWithoutPostsIsRefused()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "x");
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).PublishFolder(empty);
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("нет файлов постов", res);
        Assert.Empty(dmc.Starts);
    }

    [Fact]
    public async Task AMissingOrBrokenManifestStopsBeforeUploading()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).PublishFolder(_dir, Path.Combine(_dir, "nope.csv"));
        Assert.StartsWith("ОШИБКА", res);
        Assert.Contains("manifest", res);
        res = await Tools(dmc).PublishFolder(_dir, Manifest("name\nx\n"));
        Assert.StartsWith("ОШИБКА", res);
        Assert.Empty(dmc.Starts);
    }

    /** Лишняя строка в manifest — предупреждение в отчёте, а не отказ: остальное заливается. */
    [Fact]
    public async Task AManifestRowForAMissingFileIsReportedNotFatal()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Done(
            new ImportComponent("a", "POST_PROCESSOR", "p1", false),
            new ImportComponent("b", "POST_PROCESSOR", "p2", false)));
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "a");
        dmc.Products["p2"] = FakeDmcClient.Post("p2", "b");

        var res = await Tools(dmc).PublishFolder(_dir, Manifest("file,name\nc.sppx,Missing\n"));

        Assert.Single(dmc.Starts);
        Assert.Contains("c.sppx", res);
        Assert.Contains("нет в папке", res);
    }
}
