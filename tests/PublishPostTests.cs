using DmcMcp;
using Xunit;

public class PublishPostTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dmc-" + Guid.NewGuid().ToString("N")[..8] + ".sppx");
    public PublishPostTests() => File.WriteAllText(_file, "x");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    /** The usual path: the import runs, finishes, a draft is created — the agent gets the link and what to do next. */
    [Fact]
    public async Task CreatesADraftAndReportsTheLink()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Running());
        dmc.Progress.Enqueue(FakeDmcClient.Done(new ImportComponent("Fanuc 0i", "POST_PROCESSOR", "p1", AiEnriched: true)));
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", slug: "fanuc-0i");

        var res = await Tools(dmc).PublishPost(_file, name: "Fanuc 0i-MF", descriptionHint: "für Haas");

        Assert.Contains("Draft created", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
        Assert.Contains("submit_post", res);
        Assert.Contains("Cover: no", res);
        var s = Assert.Single(dmc.Starts);
        Assert.Equal(("Fanuc 0i-MF", "für Haas", true), (s.Name, s.Desc, s.Ai));
        Assert.Equal(32, s.ImportId.Length);
    }

    [Fact]
    public async Task RefusesWhenThereIsNoLogin()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc, token: null).PublishPost(_file);
        Assert.StartsWith("ERROR", res);
        Assert.Contains("dmc-mcp login", res);
        Assert.Empty(dmc.Starts);
    }

    [Fact]
    public async Task RefusesAMissingFileAndAWrongType()
    {
        var dmc = new FakeDmcClient();
        Assert.Contains("does not exist", await Tools(dmc).PublishPost(_file + ".missing"));
        var txt = Path.ChangeExtension(_file, ".txt");
        File.WriteAllText(txt, "x");
        try { Assert.Contains("not a post", await Tools(dmc).PublishPost(txt)); }
        finally { File.Delete(txt); }
        Assert.Empty(dmc.Starts);
    }

    /** The backend allows one import per publisher — a 409 is explained in words, not as a code. */
    [Fact]
    public async Task ABusyImportIsExplained()
    {
        var dmc = new FakeDmcClient { FailStart = new DmcHttpException(409, """{"error":"An import is already running"}""") };
        var res = await Tools(dmc).PublishPost(_file);
        Assert.Contains("an import running", res);
    }

    [Fact]
    public async Task AServerSideFailureNamesTheReason()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Failed("AI provider unreachable"));
        var res = await Tools(dmc).PublishPost(_file);
        Assert.StartsWith("ERROR: import failed", res);
        Assert.Contains("AI provider unreachable", res);
    }

    [Fact]
    public async Task NothingCreatedListsTheErrors()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.DoneWithErrors("post.sppx: no recognised component files"));
        var res = await Tools(dmc).PublishPost(_file);
        Assert.Contains("did not find", res);
        Assert.Contains("no recognised component files", res);
    }

    /** A long import does not hold the call forever: hand back the importId, say the server carries on. */
    [Fact]
    public async Task AnEndlessImportReturnsTheImportId()
    {
        var dmc = new FakeDmcClient();
        dmc.Progress.Enqueue(FakeDmcClient.Running("post.sppx"));
        var tools = Tools(dmc);
        tools.MaxWait = TimeSpan.Zero;

        var res = await tools.PublishPost(_file);

        Assert.Contains("still running", res);
        Assert.Contains(dmc.Starts[0].ImportId, res);
        Assert.Contains("My components", res);
    }
}
