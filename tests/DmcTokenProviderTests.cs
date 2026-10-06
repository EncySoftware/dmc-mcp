using DmcMcp;
using Xunit;

public class DmcTokenProviderTests
{
    /** Our own token file: it must not be shared with the extension store, which uses another Keycloak. */
    [Fact]
    public void AuthFileLivesInItsOwnFolder()
    {
        Assert.EndsWith(Path.Combine("dmc-mcp", "auth.json"), DmcTokenProvider.AuthFilePath);
    }

    /** DMC_TOKEN is the way for CI and debugging: a ready token bypasses the stored sign-in and Keycloak. */
    [Fact]
    public async Task EnvTokenWinsOverEverything()
    {
        Environment.SetEnvironmentVariable("DMC_TOKEN", " env-tok ");
        try
        {
            Assert.Equal("env-tok", await new DmcTokenProvider().GetAccessToken());
        }
        finally { Environment.SetEnvironmentVariable("DMC_TOKEN", null); }
    }
}
