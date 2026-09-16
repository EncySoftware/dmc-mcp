using System.Text.Json;
using DmcMcp;
using Xunit;

/** Полная карточка: агент должен видеть текст описания, обложку, файлы и связи, а не только сводку. */
public class DescribeTests
{
    private static DmcTools Tools(FakeDmcClient dmc, string? token = "tok") =>
        new(dmc, new FakeTokens(token)) { Delay = _ => Task.CompletedTask, PollEvery = TimeSpan.Zero };

    [Fact]
    public async Task ShowsEverythingTheCardCarries()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = DmcJson.Product(JsonDocument.Parse("""
            {"id":"p1","slug":"fanuc-0i","name":"Fanuc 0i","contentType":"POST_PROCESSOR","publicationStatus":"DRAFT",
             "description":"Post for Haas VF-2 with rigid tapping.","controllerManufacturer":"Fanuc","controllerModel":"0i-MF",
             "machineManufacturer":"Haas","machineModel":"VF-2","machineType":"MILLING","numberOfAxes":3,
             "imageUrl":"products/p1/cover.png","productFile":"products/p1/post.zip",
             "sampleOutputCodeFile":"products/p1/sample.nc","priceEur":0,"trialDays":30}
            """).RootElement);
        dmc.Links["p1"] = new List<LinkInfo>
        {
            new("l1", "MADE_FOR", "OUT", FakeDmcClient.Post("s1", "Haas VF-2", status: "PUBLISHED", contentType: "MACHINE_SCHEMA")),
        };

        var res = await Tools(dmc).DescribePost("p1");

        Assert.Contains("Post for Haas VF-2 with rigid tapping.", res);
        Assert.Contains("products/p1/cover.png", res);
        Assert.Contains("products/p1/post.zip", res);
        Assert.Contains("products/p1/sample.nc", res);
        Assert.Contains("Haas VF-2", res);
        Assert.Contains("https://dmc.test/product/fanuc-0i", res);
        Assert.Contains("черновик", res);
    }

    [Fact]
    public async Task SaysWhatIsMissing()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i");
        var res = await Tools(dmc).DescribePost("p1");
        Assert.Contains("Описание: нет", res);
        Assert.Contains("Обложка: нет", res);
        Assert.Contains("Связи: нет", res);
    }

    [Fact]
    public async Task UnknownIsExplained()
    {
        Assert.Contains("не нашёл", await Tools(new FakeDmcClient()).DescribePost("nope"));
    }
}
