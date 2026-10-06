using System.Text.Json;
using DmcMcp;
using Xunit;

/** The full card: the agent must see the description text, the cover, files and links, not just a summary. */
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
        Assert.Contains("draft", res);
    }

    [Fact]
    public async Task SaysWhatIsMissing()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = FakeDmcClient.Post("p1", "Fanuc 0i");
        var res = await Tools(dmc).DescribePost("p1");
        Assert.Contains("Description: none", res);
        Assert.Contains("Cover: none", res);
        Assert.Contains("Links: none", res);
    }

    [Fact]
    public async Task UnknownIsExplained()
    {
        Assert.Contains("did not find", await Tools(new FakeDmcClient()).DescribePost("nope"));
    }
}
