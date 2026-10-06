using System.Text.Json;
using DmcMcp;
using Xunit;

public class DmcClientParsingTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ProductReadsTheFieldsTheToolsShow()
    {
        var p = DmcJson.Product(J("""
            {"id":"11111111-1111-1111-1111-111111111111","slug":"fanuc-0i","name":"Fanuc 0i",
             "contentType":"POST_PROCESSOR","publicationStatus":"DRAFT","description":"d",
             "controllerManufacturer":"Fanuc","controllerSeries":"0i","controllerModel":"MF",
             "machineManufacturer":"Haas","machineType":"MILLING","numberOfAxes":3,"imageUrl":"products/x/cover.png"}
            """));
        Assert.Equal("Fanuc 0i", p.Name);
        Assert.Equal("Fanuc 0i MF", p.Controller);
        Assert.Equal("Haas", p.Machine);
        Assert.Equal(3, p.NumberOfAxes);
        Assert.True(p.HasCover);
        Assert.Equal("https://dmc.test/product/fanuc-0i", p.Url("https://dmc.test"));
    }

    /** Without a slug the link goes by id; a slug with a space is escaped — like productUrl() in the web client. */
    [Fact]
    public void UrlFallsBackToIdAndEscapes()
    {
        Assert.Equal("https://s/product/abc", DmcJson.Product(J("""{"id":"abc"}""")).Url("https://s"));
        Assert.Equal("https://s/product/a%20b", DmcJson.Product(J("""{"id":"x","slug":"a b"}""")).Url("https://s"));
        Assert.Equal("—", DmcJson.Product(J("""{"id":"x"}""")).Controller);
    }

    [Fact]
    public void ProgressReadsComponentsAndErrors()
    {
        var pr = DmcJson.Progress(J("""
            {"importId":"i1","status":"done","done":1,"total":1,"currentName":"",
             "result":{"components":[{"name":"Fanuc 0i","contentType":"POST_PROCESSOR","productId":"p1","aiEnriched":true}],
                       "errors":[{"component":"bad.zip","message":"no component files"}]}}
            """));
        Assert.True(pr.Finished);
        Assert.Single(pr.Components);
        Assert.Equal("p1", pr.Components[0].ProductId);
        Assert.Equal("bad.zip: no component files", pr.Errors[0]);
        Assert.False(DmcJson.Progress(J("""{"status":"queued"}""")).Finished);
    }

    /** A field the tool does not know (new on the backend) goes back in the PUT instead of being nulled. */
    [Fact]
    public void RequestFromRoundTripsUnknownFields()
    {
        var req = DmcJson.RequestFrom(J("""{"id":"p1","name":"N","brandNewField":"keep me","updatedAt":"2026-09-16"}"""));
        Assert.Equal("keep me", ((JsonElement)req["brandNewField"]!).GetString());
        Assert.False(req.ContainsKey("updatedAt"));
        Assert.False(req.ContainsKey("id"));
    }

    /** A full PUT takes only request fields: the backend would not accept the card's id, slug and counters. */
    [Fact]
    public void RequestFromKeepsOnlyRequestFields()
    {
        var req = DmcJson.RequestFrom(J("""
            {"id":"p1","slug":"s","name":"N","productFile":"products/p1/post.zip","downloadCount":5,
             "hasProductFile":true,"description":null,"numberOfAxes":3}
            """));
        Assert.Equal(new[] { "name", "numberOfAxes", "productFile" }, req.Keys.OrderBy(k => k).ToArray());
    }
}

/** Files go to DMC from the disk as they are read: on the hosted server one can weigh a gigabyte. */
public class DmcClientUploadTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dmc-up-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    [Fact]
    public void TheFileIsStreamedNotLoaded()
    {
        using (var f = File.Create(_file)) f.SetLength(32L * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        using (var form = DmcClient.FileForm(_file))
        {
            Assert.InRange(form.Headers.ContentLength ?? 0, 32L * 1024 * 1024, 32L * 1024 * 1024 + 1000); // known up front
            form.CopyTo(Stream.Null, null, default);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"{allocated / (1024 * 1024)} MB allocated for a 32 MB file");
        File.Delete(_file); // the form let go of it
    }
}
