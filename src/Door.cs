using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DmcMcp;

/// <summary>
/// The hosted server's door for /mcp and /mcp/upload. Two credentials let a request in, the cheapest check first:
/// the key (<see cref="KeyAuth"/>: header or path, constant-time) — the call then runs as the server's own signed-in
/// account; or `Authorization: Bearer` with a person's access token from the trusted realm (<see cref="UserTokens"/>)
/// — the call runs as that person. A bearer that is not the key and looks like a JWT is checked as a token, and it
/// decides even when the key is in the path too: the shared account never stands in for a person, and a refused
/// token is a 401 even then. The caller is made current for the request (<see cref="Caller.Enter"/>). The resource
/// metadata paths are public. Tokens never reach a log: a refusal is logged by its reason.
/// </summary>
public sealed class Door(string key, UserTokens? tokens, string? metadataUrl, ILogger log)
{
    public const string MetadataPath = "/.well-known/oauth-protected-resource";
    public const string McpMetadataPath = MetadataPath + "/mcp";

    public async Task Handle(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path is MetadataPath or McpMetadataPath)
        {
            await next(); // how a client learns where to sign in: no credentials needed
            return;
        }
        if (!KeyAuth.UnderMcp(path))
        {
            await Refuse(ctx, null, null);
            return;
        }

        var (keyInPath, rest) = KeyAuth.InPath(path, key);
        var bearer = KeyAuth.Bearer(ctx.Request.Headers.Authorization.ToString());
        Caller caller;
        if (bearer != null && KeyAuth.IsKey(bearer, key)) caller = Caller.Server;
        else if (bearer != null && tokens != null && LooksLikeJwt(bearer))
        {
            var check = await tokens.Check(bearer, ctx.RequestAborted);
            if (check.Unavailable)
            {
                log.LogWarning("Tokens cannot be checked: the keys of {Issuer} could not be fetched ({Error}); answering 503",
                    tokens.Settings.Issuer, tokens.KeysError);
                await Unavailable(ctx);
                return;
            }
            if (check.Caller == null)
            {
                log.LogInformation("Refused a token: {Reason}", check.Refusal);
                await Refuse(ctx, "invalid_token", check.Refusal);
                return;
            }
            caller = check.Caller;
        }
        else if (keyInPath) caller = Caller.Server;
        else
        {
            await Refuse(ctx, bearer == null ? null : "invalid_token", bearer == null ? null : "unknown key or malformed token");
            return;
        }

        ctx.Request.Path = rest;
        using (Caller.Enter(caller)) await next();
    }

    /** Three base64url parts: a JWS. A key is one opaque string (openssl rand -hex 32). */
    internal static bool LooksLikeJwt(string bearer) => bearer.Count(c => c == '.') == 2;

    /**
     * 401. With tokens on, the challenge names the resource metadata (MCP authorization, RFC 9728) and, for a bad
     * credential, error="invalid_token" with a reason (RFC 6750) — fixed ASCII text, never the credential. With
     * tokens off, exactly 0.8.0's bare challenge.
     */
    private Task Refuse(HttpContext ctx, string? error, string? description)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (tokens == null)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }
        var parts = new List<string>();
        if (error != null) parts.Add($"error=\"{error}\"");
        if (description != null) parts.Add($"error_description=\"{description}\"");
        parts.Add($"resource_metadata=\"{metadataUrl}\"");
        ctx.Response.Headers.WWWAuthenticate = "Bearer " + string.Join(", ", parts);
        return ctx.Response.WriteAsJsonAsync(new Dictionary<string, string>
        {
            ["error"] = error ?? "unauthorized",
            ["error_description"] = description
                ?? "send Authorization: Bearer with your DMC access token, or the server's key",
        });
    }

    /** No keys to check a token with (Keycloak unreachable on first use): not the token's fault, so not a 401. */
    private static Task Unavailable(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "60";
        return ctx.Response.WriteAsJsonAsync(new Dictionary<string, string>
        {
            ["error"] = "temporarily_unavailable",
            ["error_description"] = "the sign-in server's keys could not be fetched, so tokens cannot be checked; try again in a minute",
        });
    }
}
