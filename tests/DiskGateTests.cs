using DmcMcp;
using Xunit;

/**
 * What a person may cost the hosted server's own disk. A licsys token alone buys nothing there: only someone DMC lets
 * publish has files stored, downloaded or unpacked for them — one at a time. The key is the operator's own.
 */
public class DiskGateTests
{
    private static readonly Caller Anna = Caller.Person("tok-anna", "anna@example.com", TestIssuer.Issuer, "sub-anna");
    private static readonly Caller Boris = Caller.Person("tok-boris", "boris@example.com", TestIssuer.Issuer, "sub-boris");

    private static DiskGate Gate(FakeDmcClient dmc) => new(new Who(dmc));

    private static FakeDmcClient Roles(params string[] roles) =>
        new() { Who = new MeInfo("anna@example.com", roles, "acc-anna", null) };

    [Theory]
    [InlineData("DEALER")]
    [InlineData("ADMIN")]
    public async Task APublisherOrAnAdminMayStoreFiles(string role) =>
        Assert.Null(await Gate(Roles("USER", role)).Check(Anna));

    [Fact]
    public async Task SomeoneDmcDoesNotLetPublishMayNot()
    {
        var dmc = Roles("USER");
        var refused = await Gate(dmc).Check(Anna);
        Assert.NotNull(refused);
        Assert.Equal(403, refused!.Status);
        Assert.Contains("anna@example.com", refused.Message);
        Assert.Contains("not a Publisher", refused.Message);
        Assert.Equal(new[] { "tok-anna" }, dmc.TokensOf("Me"));
    }

    /** DMC does not answer, or does not take the token: nothing is stored on a guess. */
    [Fact]
    public async Task WhenDmcCannotSayWhoItIsTheAnswerIsNotYet()
    {
        var refused = await Gate(new FakeDmcClient { FailMe = new DmcHttpException(502, "") }).Check(Anna);
        Assert.NotNull(refused);
        Assert.Equal(503, refused!.Status);
        Assert.Contains("did not confirm", refused.Message);
    }

    /** The key's account is the operator's: DMC is not asked. */
    [Fact]
    public async Task TheKeyIsNotAsked()
    {
        var dmc = Roles("USER");
        Assert.Null(await Gate(dmc).Check(Caller.Server));
        Assert.Equal(0, dmc.MeCalls);
    }

    /** Asked once a minute per token, not once per file. */
    [Fact]
    public async Task DmcIsAskedOncePerToken()
    {
        var dmc = Roles("USER", "DEALER");
        var gate = Gate(dmc);
        for (var i = 0; i < 3; i++) Assert.Null(await gate.Check(Anna));
        Assert.Equal(1, dmc.MeCalls);
    }

    [Fact]
    public void APersonRunsOneDownloadOrUnpackingAtATimeAndTwoRunInAll()
    {
        var gate = Gate(Roles("DEALER"));
        var annas = gate.TryBegin(Anna, out var none);
        Assert.NotNull(annas);
        Assert.Null(none);
        Assert.Null(gate.TryBegin(Anna, out var mine));
        Assert.Contains("you already have a download or an unpacking running", mine);
        using var boris = gate.TryBegin(Boris, out _);
        Assert.NotNull(boris);
        var carol = Caller.Person("tok-carol", "carol@example.com", TestIssuer.Issuer, "sub-carol");
        Assert.Null(gate.TryBegin(carol, out var everyone));
        Assert.Contains("already downloading or unpacking", everyone);
        Assert.Null(gate.TryBegin(Caller.Server, out _)); // the total binds the key too
        annas!.Dispose();
        using var again = gate.TryBegin(Anna, out _);
        Assert.NotNull(again);
    }

    /** The key is the operator's own: bound by the total alone. */
    [Fact]
    public void TheKeyIsBoundByTheTotalAlone()
    {
        var gate = Gate(Roles("DEALER"));
        using var a = gate.TryBegin(Caller.Server, out _);
        using var b = gate.TryBegin(Caller.Server, out _);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}
