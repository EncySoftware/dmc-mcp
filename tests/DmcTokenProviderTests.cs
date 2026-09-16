using DmcMcp;
using Xunit;

public class DmcTokenProviderTests
{
    /** Файл токена свой: делить его с магазином расширений нельзя — там другой Keycloak. */
    [Fact]
    public void AuthFileLivesInItsOwnFolder()
    {
        Assert.EndsWith(Path.Combine("dmc-mcp", "auth.json"), DmcTokenProvider.AuthFilePath);
    }

    /** DMC_TOKEN — путь для CI и отладки: готовый токен обходит хранилище и Keycloak. */
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
