using System.Globalization;
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

    /**
     * The price reads in the currency DMC names beside it. priceEur is a historical name: since DMC's payments release
     * the number is US dollars and priceCurrency says "USD"; an older DMC sends no priceCurrency, and its numbers are
     * euros. 0 and -1 stay words whatever the currency.
     */
    [Theory]
    [InlineData("19.00", "\"USD\"", "Price: $19.00, trial 30 days")]
    [InlineData("19.00", "\"usd\"", "Price: $19.00, trial 30 days")]
    [InlineData("19.00", null, "Price: 19.00 €, trial 30 days")] // an older DMC: no priceCurrency at all
    [InlineData("19.00", "\"\"", "Price: 19.00 €, trial 30 days")]
    [InlineData("19.00", "null", "Price: 19.00 €, trial 30 days")]
    [InlineData("19.00", "\"GBP\"", "Price: 19.00 GBP, trial 30 days")]
    [InlineData("0.00", "\"USD\"", "Price: free, trial 30 days")]
    [InlineData("-1.00", "\"USD\"", "Price: included in maintenance, trial 30 days")]
    public async Task PriceReadsInTheCurrencyDmcNames(string priceEur, string? priceCurrency, string line)
    {
        var dmc = new FakeDmcClient();
        dmc.Products["p1"] = Priced(priceEur, priceCurrency);
        Assert.Equal(line, PriceLine(await Tools(dmc).DescribePost("p1")));
    }

    /** The number reads the same on every machine: "$19.50", never "$19,50" where the culture writes a decimal comma. */
    [Fact]
    public async Task PriceReadsWithADecimalPointWhateverTheCulture()
    {
        var dmc = new FakeDmcClient();
        dmc.Products["usd"] = Priced("19.50", "\"USD\"");
        dmc.Products["eur"] = Priced("19.50", null);
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ","; // as ru-RU writes it; a clone needs no ICU data on the runner
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = comma;
        try
        {
            Assert.Equal("Price: $19.50, trial 30 days", PriceLine(await Tools(dmc).DescribePost("usd")));
            Assert.Equal("Price: 19.50 €, trial 30 days", PriceLine(await Tools(dmc).DescribePost("eur")));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    /** A card as DMC sends it: the price with cents (a BigDecimal of scale 2); priceCurrency is raw JSON, null leaves it out. */
    private static ProductInfo Priced(string priceEur, string? priceCurrency) => DmcJson.Product(JsonDocument.Parse($$"""
        {"id":"p1","slug":"fanuc-0i","name":"Fanuc 0i","contentType":"POST_PROCESSOR","publicationStatus":"PUBLISHED",
         "priceEur":{{priceEur}}{{(priceCurrency == null ? "" : ",\"priceCurrency\":" + priceCurrency)}},"trialDays":30}
        """).RootElement);

    private static string PriceLine(string describe) =>
        describe.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("Price: "));
}
