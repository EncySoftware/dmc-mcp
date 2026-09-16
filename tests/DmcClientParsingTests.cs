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

    /** Без slug ссылка ведёт по id; slug с пробелом экранируется — как productUrl() у веб-клиента. */
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

    /** Для полного PUT берутся только поля запроса: id, slug и счётчики карточки бэкенд не примет. */
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
