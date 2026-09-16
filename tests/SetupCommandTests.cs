using DmcMcp;
using System.Text.Json;
using Xunit;

public class SetupCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-setup-" + Guid.NewGuid().ToString("N"));
    private string CursorConfig => Path.Combine(_dir, "mcp.json");

    public SetupCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<(int Code, string Out)> Run(FakeProcessRunner? proc = null, bool hasLogin = true,
                                                  bool noLogin = false, Action? onLogin = null)
    {
        var lines = new List<string>();
        int code = await SetupCommand.Run(CursorConfig, proc ?? new FakeProcessRunner(),
            () => hasLogin, () => { onLogin?.Invoke(); return Task.FromResult(0); }, noLogin, lines.Add);
        return (code, string.Join("\n", lines));
    }

    private static string Server(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty(name)
            .GetProperty("command").GetString()!;

    [Fact]
    public async Task WritesTheServerIntoAFreshConfig()
    {
        var (code, output) = await Run();

        Assert.Equal(0, code);
        Assert.Equal("dmc-mcp", Server(File.ReadAllText(CursorConfig), "dmc"));
        Assert.Contains("restart", output, StringComparison.OrdinalIgnoreCase);
    }

    /** Somebody else's MCP servers must survive — this file is shared by every tool the author uses. */
    [Fact]
    public async Task KeepsOtherServersAndUnknownFields()
    {
        File.WriteAllText(CursorConfig, """
            {"mcpServers":{"figma":{"command":"figma-mcp","args":["--stdio"]}},"someOtherSetting":42}
            """);

        await Run();

        string json = File.ReadAllText(CursorConfig);
        Assert.Equal("figma-mcp", Server(json, "figma"));
        Assert.Equal("dmc-mcp", Server(json, "dmc"));
        Assert.Equal(42, JsonDocument.Parse(json).RootElement.GetProperty("someOtherSetting").GetInt32());
    }

    [Fact]
    public async Task RunningItTwiceChangesNothing()
    {
        await Run();
        string first = File.ReadAllText(CursorConfig);
        var (code, output) = await Run();

        Assert.Equal(0, code);
        Assert.Equal(first, File.ReadAllText(CursorConfig));
        Assert.Contains("already", output, StringComparison.OrdinalIgnoreCase);
    }

    /** A broken config is the author's file: report it, never overwrite it. */
    [Fact]
    public async Task RefusesToTouchAMalformedConfig()
    {
        File.WriteAllText(CursorConfig, "{ not json");
        var (code, output) = await Run();

        Assert.NotEqual(0, code);
        Assert.Equal("{ not json", File.ReadAllText(CursorConfig));
        Assert.Contains(CursorConfig, output);
    }

    [Fact]
    public async Task RegistersWithClaudeCodeWhenItsCliIsThere()
    {
        var proc = new FakeProcessRunner()
            .On("claude --version", stdout: "1.0.0")
            .On("claude mcp add");
        var (_, output) = await Run(proc);

        Assert.Contains("Claude Code", output);
    }

    [Fact]
    public async Task SkipsClaudeCodeSilentlyWhenAbsent()
    {
        var (_, output) = await Run(new FakeProcessRunner());   // every call fails => no CLI
        Assert.DoesNotContain("Claude Code", output);
    }

    [Fact]
    public async Task OffersTheStoreLoginWhenThereIsNone()
    {
        bool loggedIn = false;
        var (code, _) = await Run(hasLogin: false, onLogin: () => loggedIn = true);

        Assert.Equal(0, code);
        Assert.True(loggedIn, "setup is the one moment the author is at a terminal — log in here");
    }

    [Fact]
    public async Task DoesNotLoginWhenAlreadyLoggedInOrWhenAskedNotTo()
    {
        bool loggedIn = false;
        await Run(hasLogin: true, onLogin: () => loggedIn = true);
        Assert.False(loggedIn);

        await Run(hasLogin: false, noLogin: true, onLogin: () => loggedIn = true);
        Assert.False(loggedIn);
    }
}
