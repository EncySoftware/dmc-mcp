using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DmcMcp;

/// <summary>
/// The hosted server signs in once (`login --password` inside the container) and keeps only an offline refresh
/// token. An offline session dies when idle, so this refreshes it every 12 hours whether or not anyone calls the
/// server, and says in the log whom the server acts as — the line an operator looks for in `docker logs`. Until
/// that line is a publisher's, it checks every minute: the operator signs in after the container has started, and
/// Keycloak may be unreachable for a moment. A failure is a line in the log, never an exception — out of a
/// BackgroundService one stops the whole host.
/// </summary>
public sealed class SignInKeepAlive(IDmcClient dmc, DmcTokenProvider tokens, ILogger<SignInKeepAlive> log) : BackgroundService
{
    public const string HostedLoginCommand = "docker exec -it dmc-mcp dotnet /app/dmc-mcp.dll login --password";
    internal const string SignedInAs = "Signed in to DMC as ";
    internal static readonly TimeSpan Every = TimeSpan.FromHours(12);
    internal static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    /** Replaced in tests. */
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        string? last = null;
        while (!stop.IsCancellationRequested)
        {
            string line;
            try { line = await Describe(dmc, tokens); }
            catch (Exception e) { line = "DMC sign-in could not be checked: " + e.Message; }
            if (line != last) log.LogWarning("{Line}", line); // a minute's re-check repeats nothing
            last = line;
            var ready = line.StartsWith(SignedInAs, StringComparison.Ordinal) && !line.Contains("WARNING");
            try { await Delay(ready ? Every : Retry, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static async Task<string> Describe(IDmcClient dmc, DmcTokenProvider tokens)
    {
        string? token;
        try { token = await tokens.GetAccessToken(); }
        catch (InvalidOperationException e) { return $"DMC sign-in is broken ({e.Message}) — run: {HostedLoginCommand}"; }
        catch (Exception e) { return $"DMC sign-in could not be checked — Keycloak did not answer ({e.Message}); trying again in a minute."; }
        if (token == null) return $"NOT signed in to DMC — run once: {HostedLoginCommand}";
        try
        {
            var me = await dmc.Me(token);
            var roles = me.Roles.Count == 0 ? "none" : string.Join(", ", me.Roles);
            var line = $"{SignedInAs}{me.Username}, roles: {roles}";
            return me.Roles.Contains("DEALER", StringComparer.OrdinalIgnoreCase)
                ? line
                : line + " — WARNING: no Publisher role, publishing will be refused; an admin grants it in DMC.";
        }
        catch (Exception e) { return "Signed in, but DMC did not answer /auth/me: " + e.Message; }
    }
}
