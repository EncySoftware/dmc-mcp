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

    /** Over HTTP two calls arrive together: one refresh, not two (rotation would kill the second). */
    [Fact]
    public async Task ConcurrentCallsRefreshOnce()
    {
        var file = Path.Combine(Path.GetTempPath(), "dmc-auth-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(file, """{"refresh_token":"r1","client_id":"dealer-space"}""");
        Environment.SetEnvironmentVariable("DMC_AUTH_FILE", file);
        try
        {
            var handler = new CountingKeycloak();
            var provider = new DmcTokenProvider { Client = new HttpClient(handler) };
            var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAccessToken()));
            Assert.All(tokens, t => Assert.Equal("a1", t));
            Assert.Equal(1, handler.Calls);
            Assert.Contains("r2", File.ReadAllText(file)); // the rotated refresh token is kept
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMC_AUTH_FILE", null);
            File.Delete(file);
        }
    }

    /**
     * The refresh token is an offline session: whoever reads the file acts as the account. Under a umask of 022 it
     * came out 0644 — on the hosted server readable by every user of the VPS. Rewritten, it is the owner's alone.
     * (Unix only: the test passes vacuously on Windows, where the profile folder is the user's.)
     */
    [Fact]
    public async Task TheStoredTokenIsReadableByItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "dmc-auth-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(dir, "dmc-mcp", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"refresh_token":"r1","client_id":"dealer-space"}""");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Environment.SetEnvironmentVariable("DMC_AUTH_FILE", file);
        try
        {
            await new DmcTokenProvider { Client = new HttpClient(new CountingKeycloak()) }.GetAccessToken();
            Assert.Contains("r2", File.ReadAllText(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMC_AUTH_FILE", null);
            Directory.Delete(dir, true);
        }
    }

    private sealed class CountingKeycloak : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(50, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"a1","expires_in":300,"refresh_token":"r2"}"""),
            };
        }
    }
}
