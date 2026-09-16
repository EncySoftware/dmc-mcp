using System.Text.Json;

namespace DmcMcp;

/// <summary>
/// `dmc-mcp doctor` — по строке на проверку: вход, что DMC принимает токен и даёт роль паблишера,
/// что сервер прописан в редакторах. Первый вопрос от пользователей будет «у меня не работает»;
/// этот вывод — ответ на него.
/// </summary>
public static class Doctor
{
    public static async Task<int> Run(IDmcClient dmc, DmcTokenProvider tokens, IProcessRunner proc,
        string cursorConfigPath, Action<string> write)
    {
        bool bad = false;
        void Ok(string s) => write("✓ " + s);
        void Fail(string s) { bad = true; write("✗ " + s); }
        void Info(string s) => write("— " + s);

        Ok($".NET {Environment.Version}, {Brand.Cli} {VersionCheck.Current}");

        string? token = null;
        bool loginBroken = false;
        try { token = await tokens.GetAccessToken(); }
        catch (InvalidOperationException e) { loginBroken = true; Fail("вход протух: " + e.Message); }
        if (token == null && !loginBroken) Fail($"входа нет — выполните `{Brand.Cli} login`");
        else if (token != null) Ok("вход есть (" + DmcTokenProvider.AuthFilePath + ")");

        if (token != null)
        {
            try
            {
                var me = await dmc.Me(token);
                Ok($"DMC отвечает: вы {me.Username}, роли: {(me.Roles.Count == 0 ? "нет" : string.Join(", ", me.Roles))}");
                if (!me.IsPublisher)
                    Fail("нет роли паблишера (DEALER) — публиковать не выйдет; попросите её у администратора DMC");
            }
            catch (DmcHttpException e) when (e.Status == 401)
            {
                Fail($"DMC не принял токен — выполните `{Brand.Cli} login` заново");
            }
            catch (Exception e) { Fail("DMC не отвечает: " + e.Message); }
        }

        // ---- Cursor: сервер в ~/.cursor/mcp.json
        bool cursor = false;
        try
        {
            if (File.Exists(cursorConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cursorConfigPath));
                cursor = doc.RootElement.TryGetProperty("mcpServers", out var s)
                         && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(Brand.McpServerName, out _);
            }
        }
        catch (JsonException) { /* битый файл — как отсутствие записи */ }
        if (cursor) Ok($"Cursor: сервер {Brand.McpServerName} прописан ({cursorConfigPath})");
        else Fail($"Cursor: сервера {Brand.McpServerName} нет в {cursorConfigPath} — выполните `{Brand.Cli} setup`");

        // ---- Claude Code: через его CLI; отсутствие CLI — справка, не поломка
        var version = await proc.Run("claude", "--version");
        if (!version.Ok) Info("Claude Code не установлен (или не на PATH) — это не ошибка");
        else
        {
            var list = await proc.Run("claude", "mcp list");
            if (list.Ok && list.StdOut.Contains(Brand.McpServerName)) Ok("Claude Code: сервер зарегистрирован");
            else Fail($"Claude Code: сервер не зарегистрирован — `claude mcp add {Brand.McpServerName} -- {Brand.Cli}`");
        }

        write(bad ? "Есть проблемы — см. строки с ✗." : "Всё в порядке.");
        return bad ? 1 : 0;
    }
}
