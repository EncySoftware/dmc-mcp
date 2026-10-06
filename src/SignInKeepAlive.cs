using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DmcMcp;

/// <summary>
/// The hosted server signs in once (`login --password` inside the container) and keeps only an offline refresh
/// token. An offline session dies when idle, so this refreshes it every 12 hours whether or not anyone calls the
/// server, and says at startup whom the server acts as — the line an operator looks for in `docker logs`.
/// </summary>
public sealed class SignInKeepAlive(IDmcClient dmc, DmcTokenProvider tokens, ILogger<SignInKeepAlive> log) : BackgroundService
{
    public const string HostedLoginCommand = "docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password";
    internal static readonly TimeSpan Every = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            log.LogWarning("{Line}", await Describe(dmc, tokens));
            try { await Task.Delay(Every, stop); }
            catch (TaskCanceledException) { return; }
        }
    }

    internal static async Task<string> Describe(IDmcClient dmc, DmcTokenProvider tokens)
    {
        string? token;
        try { token = await tokens.GetAccessToken(); }
        catch (InvalidOperationException e) { return $"DMC sign-in is broken ({e.Message}) — run: {HostedLoginCommand}"; }
        if (token == null) return $"NOT signed in to DMC — run once: {HostedLoginCommand}";
        try
        {
            var me = await dmc.Me(token);
            var roles = me.Roles.Count == 0 ? "none" : string.Join(", ", me.Roles);
            var line = $"Signed in to DMC as {me.Username}, roles: {roles}";
            return me.Roles.Contains("DEALER", StringComparer.OrdinalIgnoreCase)
                ? line
                : line + " — WARNING: no Publisher role, publishing will be refused; an admin grants it in DMC.";
        }
        catch (Exception e) { return "Signed in, but DMC did not answer /auth/me: " + e.Message; }
    }
}
