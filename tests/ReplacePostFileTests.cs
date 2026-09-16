using System.Text.Json;
using DmcMcp;
using Xunit;

/**
 * Новая версия существующего поста: файл во временное хранилище, потом полный PUT с его путём.
 * Перенос из tmp/, удаление старого архива и обновление лицензионного контейнера делает бэкенд.
 */
public class ReplacePostFileTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dmc-" + Guid.NewGuid().ToString("N")[..8] + ".sppx");
    public ReplacePostFileTests() => File.WriteAllText(_file, "x");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    private static FakeDmcClient WithPost(string status = "DRAFT")
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i", status: status, slug: "fanuc-0i");
        return dmc;
    }

    [Fact]
    public async Task UploadsAndPutsTheNewArchivePath()
    {
        var dmc = WithPost();
        var res = await Tools(dmc).ReplacePostFile("p1", _file);

        Assert.Equal(Path.GetFileName(_file), Assert.Single(dmc.Uploads));
        var (id, body) = Assert.Single(dmc.Puts);
        Assert.Equal("p1", id);
        Assert.Equal("tmp/u1/" + Path.GetFileName(_file), body["productFile"]);
        // Остальная карточка едет как была — PUT полный.
        Assert.Equal("Fanuc 0i", ((JsonElement)body["name"]!).GetString());
        Assert.Contains("заменён", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
    }

    /** У опубликованного поста новый архив уходит в каталог без повторной модерации — об этом говорим. */
    [Fact]
    public async Task APublishedPostIsToldTheArchiveGoesLiveAtOnce()
    {
        var res = await Tools(WithPost("PUBLISHED")).ReplacePostFile("p1", _file);
        Assert.Contains("сразу", res);
    }

    [Fact]
    public async Task RefusesAWrongFileTypeBeforeUploading()
    {
        var dmc = WithPost();
        var txt = Path.ChangeExtension(_file, ".txt");
        File.WriteAllText(txt, "x");
        try { Assert.Contains("не пост", await Tools(dmc).ReplacePostFile("p1", txt)); }
        finally { File.Delete(txt); }
        Assert.Empty(dmc.Uploads);
    }

    /** Карточку читаем ДО загрузки: чужой или несуществующий id не должен стоить лишнего файла в tmp/. */
    [Fact]
    public async Task UnknownPostIsAnErrorAndNothingIsUploaded()
    {
        var dmc = new FakeDmcClient();
        var res = await Tools(dmc).ReplacePostFile("nope", _file);
        Assert.Contains("не нашёл", res);
        Assert.Empty(dmc.Uploads);
    }

    [Fact]
    public async Task RefusesWithoutLogin()
    {
        var dmc = WithPost();
        var res = await Tools(dmc, token: null).ReplacePostFile("p1", _file);
        Assert.Contains("dmc-mcp login", res);
        Assert.Empty(dmc.Uploads);
    }
}
