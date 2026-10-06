using DmcMcp;
using Xunit;

/** The hosted server's log says once at startup whom it acts as — the only check its operator needs. */
public class SignInKeepAliveTests
{
    [Fact]
    public async Task NotSignedInPointsAtTheCommand()
    {
        var line = await SignInKeepAlive.Describe(new FakeDmcClient(), new FakeTokens(null));
        Assert.Contains("NOT signed in", line);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, line);
    }

    [Fact]
    public async Task PublisherIsReported()
    {
        var client = new FakeDmcClient { Who = new MeInfo("hermes@encycam.com", new[] { "DEALER" }, null, null) };
        var line = await SignInKeepAlive.Describe(client, new FakeTokens("tok"));
        Assert.Contains("hermes@encycam.com", line);
        Assert.DoesNotContain("WARNING", line);
    }

    [Fact]
    public async Task MissingPublisherRoleIsAWarning()
    {
        var client = new FakeDmcClient { Who = new MeInfo("hermes@encycam.com", new[] { "USER" }, null, null) };
        var line = await SignInKeepAlive.Describe(client, new FakeTokens("tok"));
        Assert.Contains("WARNING", line);
        Assert.Contains("Publisher", line);
    }
}
