using DmcMcp;
using Xunit;

/**
 * `dmc-mcp doctor` — the first thing users will say is "it doesn't work for me". It checks the sign-in,
 * that DMC accepts the token and grants the publisher role, and that the server is registered in the editors.
 */
public class DoctorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-doc-" + Guid.NewGuid().ToString("N")[..8]);
    private string CursorConfig => Path.Combine(_dir, "mcp.json");
    private string CodexHome => Path.Combine(_dir, "codex");

    public DoctorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<(int Code, string Out)> Run(FakeDmcClient dmc, string? token = "tok",
        FakeProcessRunner? proc = null, bool cursorConfigured = true, string? codexConfig = null)
    {
        if (cursorConfigured)
            File.WriteAllText(CursorConfig, """{"mcpServers":{"dmc":{"command":"dmc-mcp"}}}""");
        if (codexConfig != null)
        {
            Directory.CreateDirectory(CodexHome);
            File.WriteAllText(Path.Combine(CodexHome, "config.toml"), codexConfig);
        }
        var lines = new List<string>();
        int code = await Doctor.Run(dmc, new FakeTokens(token),
            proc ?? new FakeProcessRunner().On("claude --version", stdout: "1.0").On("claude mcp list", stdout: "dmc: dmc-mcp - ✔ Connected"),
            CursorConfig, CodexHome, lines.Add);
        return (code, string.Join("\n", lines));
    }

    [Fact]
    public async Task CodexWithTheServerIsATick()
    {
        var (code, output) = await Run(new FakeDmcClient(), codexConfig: "[mcp_servers.dmc]\ncommand = \"dmc-mcp\"\n");
        Assert.Equal(0, code);
        Assert.Contains("✓ Codex", output);
    }

    [Fact]
    public async Task CodexWithoutTheServerPointsAtSetup()
    {
        var (code, output) = await Run(new FakeDmcClient(), codexConfig: "model = \"gpt-5\"\n");
        Assert.Equal(1, code);
        Assert.Contains("✗ Codex", output);
        Assert.Contains("dmc-mcp setup", output);
    }

    /** Codex may not be installed — that is information, not a failure, just as with Claude Code. */
    [Fact]
    public async Task CodexAbsentIsInformationNotFailure()
    {
        var (code, output) = await Run(new FakeDmcClient());
        Assert.Equal(0, code);
        Assert.Contains("— Codex", output);
    }

    [Fact]
    public async Task EverythingFineIsAllTicks()
    {
        var (code, output) = await Run(new FakeDmcClient());
        Assert.Equal(0, code);
        Assert.Contains("tester", output);
        Assert.Contains("DEALER", output);
        Assert.DoesNotContain("✗", output);
    }

    [Fact]
    public async Task NoLoginIsTheFirstThingNamed()
    {
        var (code, output) = await Run(new FakeDmcClient(), token: null);
        Assert.Equal(1, code);
        Assert.Contains("dmc-mcp login", output);
    }

    [Fact]
    public async Task ARejectedTokenIsNamed()
    {
        var (code, output) = await Run(new FakeDmcClient { FailMe = new DmcHttpException(401, "") });
        Assert.Equal(1, code);
        Assert.Contains("✗", output);
        Assert.Contains("login", output);
    }

    [Fact]
    public async Task NoPublisherRoleIsNamed()
    {
        var dmc = new FakeDmcClient { Who = new MeInfo("reader", new[] { "USER" }, "acc-2", null) };
        var (code, output) = await Run(dmc);
        Assert.Equal(1, code);
        Assert.Contains("publisher", output);
    }

    [Fact]
    public async Task MissingCursorEntryPointsAtSetup()
    {
        var (code, output) = await Run(new FakeDmcClient(), cursorConfigured: false);
        Assert.Equal(1, code);
        Assert.Contains("setup", output);
    }

    /** Claude Code may not be installed — that is information, not a failure. */
    [Fact]
    public async Task ClaudeCodeAbsentIsInformationNotFailure()
    {
        var (code, output) = await Run(new FakeDmcClient(), proc: new FakeProcessRunner());
        Assert.Equal(0, code);
        Assert.Contains("Claude Code", output);
    }
}
