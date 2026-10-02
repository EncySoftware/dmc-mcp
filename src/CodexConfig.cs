using System.Text;
using System.Text.RegularExpressions;

namespace DmcMcp;

/**
 * Codex — the CLI, the IDE extension and the app alike — reads its MCP servers from one TOML file,
 * `$CODEX_HOME/config.toml`, `~/.codex/config.toml` by default. It is the author's file and holds
 * their own settings, so ours is appended as one table and the rest is never rewritten; a server
 * already there under our name, in any TOML spelling, is left alone.
 *
 * <p>Asked for 2026-10-02: Yuriy's Codex could not add the server itself — its sandbox would not
 * let it open its own config.toml — and `setup` only knew Cursor and Claude Code.
 */
public static class CodexConfig
{
    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string ConfigPath(string home) => Path.Combine(home, "config.toml");

    /** `[mcp_servers.dmc]`, a dotted `mcp_servers.dmc.command = …`, or `dmc = { … }` under `[mcp_servers]`. */
    public static bool HasServer(string toml, string name)
    {
        string n = Regex.Escape(name);
        if (Regex.IsMatch(toml, $@"(?m)^[ \t]*\[[ \t]*mcp_servers[ \t]*\.[ \t]*(""{n}""|'{n}'|{n})[ \t]*\]")) return true;
        if (Regex.IsMatch(toml, $@"(?m)^[ \t]*mcp_servers[ \t]*\.[ \t]*(""{n}""|'{n}'|{n})[ \t]*[.=]")) return true;
        var table = Regex.Match(toml, @"(?ms)^[ \t]*\[[ \t]*mcp_servers[ \t]*\][^\n]*\n(.*?)(?=^[ \t]*\[|\z)");
        return table.Success
               && Regex.IsMatch(table.Groups[1].Value, $@"(?m)^[ \t]*(""{n}""|'{n}'|{n})[ \t]*[.=]");
    }

    /** True when the table was added, false when the server was already there. */
    public static bool Ensure(string home, string name, string command)
    {
        string path = ConfigPath(home);
        string text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (HasServer(text, name)) return false;

        // Keep the file's own line endings: Codex reads either, a diff of the author's file should not.
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder(text);
        if (text.Length > 0)
        {
            if (!text.EndsWith('\n')) sb.Append(nl);
            sb.Append(nl);
        }
        sb.Append($"[mcp_servers.{name}]{nl}command = \"{command}\"{nl}");

        Directory.CreateDirectory(home);
        File.WriteAllText(path, sb.ToString());
        return true;
    }
}
