using System.Text.Json;
using System.Text.Json.Nodes;

namespace DmcMcp;

/**
 * `dmc-mcp setup` — wire this server into the author's editor and log in, so getting
 * started is two commands (install, setup) instead of hand-editing JSON and remembering `login`.
 *
 * Cursor's config is shared with every other MCP server the author uses, so the file is merged,
 * never rewritten. Claude Code is registered through its own CLI when that CLI is present.
 */
public static class SetupCommand
{
    public const string ServerName = Brand.McpServerName;
    public const string Command = Brand.Cli;

    public static string DefaultCursorConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json");

    public static async Task<int> Run(string cursorConfigPath, IProcessRunner proc,
                                     Func<bool> hasLogin, Func<Task<int>> login,
                                     bool noLogin, Action<string> write)
    {
        // ---- Cursor
        JsonObject root;
        bool existed = File.Exists(cursorConfigPath);
        if (existed)
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(cursorConfigPath)) as JsonObject
                       ?? throw new JsonException("not a JSON object");
            }
            catch (JsonException e)
            {
                write($"{cursorConfigPath} is not valid JSON ({e.Message}). Fix or delete it and run setup again — "
                      + "it was left untouched.");
                return 1;
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        bool already = servers[ServerName] is JsonObject cur && (string?)cur["command"] == Command;
        if (already)
        {
            write($"Cursor: {ServerName} is already configured in {cursorConfigPath}");
        }
        else
        {
            servers[ServerName] = new JsonObject { ["command"] = Command };
            Directory.CreateDirectory(Path.GetDirectoryName(cursorConfigPath)!);
            File.WriteAllText(cursorConfigPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            write($"Cursor: {ServerName} added to {cursorConfigPath}");
        }

        // ---- Claude Code (optional; its CLI owns its own config)
        var version = await proc.Run("claude", "--version");
        if (version.Ok)
        {
            var add = await proc.Run("claude", $"mcp add {ServerName} -- {Command}");
            write(add.Ok
                ? $"Claude Code: {ServerName} registered"
                : $"Claude Code: could not register ({add.StdErr.Trim()}) — add it manually with "
                  + $"`claude mcp add {ServerName} -- {Command}`");
        }

        // ---- store login: setup is the one moment the author is at a terminal
        if (!noLogin && !hasLogin())
        {
            write("");
            write("One more thing: log in to DMC (licsys account, password is not stored).");
            int code = await login();
            if (code != 0) return code;
        }

        write("");
        write("Done — restart Cursor so it picks the server up, then ask it to publish a post.");
        return 0;
    }
}
