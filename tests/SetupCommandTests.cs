using DmcMcp;
using System.Text.Json;
using Xunit;

public class SetupCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-setup-" + Guid.NewGuid().ToString("N"));
    private string CursorConfig => Path.Combine(_dir, "mcp.json");
    /** Absent unless a test creates it — most authors have no Codex. */
    private string CodexHome => Path.Combine(_dir, "codex");
    private string CodexConfig => Path.Combine(CodexHome, "config.toml");

    public SetupCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<(int Code, string Out)> Run(FakeProcessRunner? proc = null, bool hasLogin = true,
                                                  bool noLogin = false, Action? onLogin = null)
    {
        var lines = new List<string>();
        int code = await SetupCommand.Run(CursorConfig, CodexHome, proc ?? new FakeProcessRunner(),
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

    /**
     * Codex — the CLI, the IDE extension and the app alike — reads its servers from one
     * config.toml that also holds the author's own settings: ours goes in as one more table,
     * and everything that was there stays byte for byte.
     */
    [Fact]
    public async Task AddsTheServerToCodexKeepingEverythingElse()
    {
        Directory.CreateDirectory(CodexHome);
        const string mine = "model = \"gpt-5\"\r\n\r\n[mcp_servers.figma]\r\ncommand = \"figma-mcp\"\r\n";
        File.WriteAllText(CodexConfig, mine);

        var (code, output) = await Run();

        Assert.Equal(0, code);
        string toml = File.ReadAllText(CodexConfig);
        Assert.StartsWith(mine, toml);
        Assert.EndsWith("\r\n[mcp_servers.dmc]\r\ncommand = \"dmc-mcp\"\r\n", toml);
        Assert.Contains("Codex: dmc added", output);
    }

    [Fact]
    public async Task WritesAFreshCodexConfigWhenCodexHasNoneYet()
    {
        Directory.CreateDirectory(CodexHome);

        await Run();

        Assert.Equal("[mcp_servers.dmc]\ncommand = \"dmc-mcp\"\n", File.ReadAllText(CodexConfig));
    }

    [Fact]
    public async Task CodexTwiceChangesNothing()
    {
        Directory.CreateDirectory(CodexHome);
        await Run();
        string first = File.ReadAllText(CodexConfig);

        var (_, output) = await Run();

        Assert.Equal(first, File.ReadAllText(CodexConfig));
        Assert.Contains("Codex: dmc is already configured", output);
    }

    /** Typed by hand in the other TOML spellings — still ours, still left alone. */
    [Theory]
    [InlineData("[mcp_servers]\ndmc = { command = \"dmc-mcp\" }\n")]
    [InlineData("mcp_servers.dmc.command = \"dmc-mcp\"\n")]
    [InlineData("[mcp_servers.\"dmc\"]\ncommand = \"dmc-mcp\"\n")]
    public async Task AnEntryInAnotherSpellingCountsAsConfigured(string mine)
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(CodexConfig, mine);

        await Run();

        Assert.Equal(mine, File.ReadAllText(CodexConfig));
    }

    /** A server whose name merely starts with ours is somebody else's. */
    [Fact]
    public async Task ASimilarlyNamedServerIsNotOurs()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(CodexConfig, "[mcp_servers.dmc-old]\ncommand = \"old\"\n");

        await Run();

        Assert.Contains("[mcp_servers.dmc]", File.ReadAllText(CodexConfig));
    }

    /** Codex's CLI on PATH but never started yet (no ~/.codex): the folder is its own — create it. */
    [Fact]
    public async Task RegistersWithCodexWhenOnlyItsCliIsThere()
    {
        var (_, output) = await Run(new FakeProcessRunner().On("codex --version", stdout: "codex-cli 0.50.0"));

        Assert.Contains("[mcp_servers.dmc]", File.ReadAllText(CodexConfig));
        Assert.Contains("Codex", output);
    }

    [Fact]
    public async Task SkipsCodexSilentlyWhenAbsent()
    {
        var (_, output) = await Run(new FakeProcessRunner());   // no ~/.codex, no codex CLI

        Assert.False(Directory.Exists(CodexHome));
        Assert.DoesNotContain("Codex", output);
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
