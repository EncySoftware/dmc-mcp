using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DmcMcp;

/// <summary>
/// Which access tokens the hosted server takes: those of one Keycloak realm (DMC_MCP_ISSUER, by default the realm DMC
/// itself signs in with), optionally only those minted for certain clients (DMC_MCP_CLIENTS, the azp claim) or
/// audiences (DMC_MCP_AUDIENCE, the aud claim). DMC_MCP_ISSUER=off leaves the key as the only way in.
/// </summary>
public sealed record TokenSettings(string Issuer, IReadOnlyList<string> Clients, IReadOnlyList<string> Audiences)
{
    public const string DefaultIssuer = Brand.KeycloakUrl + "realms/" + Brand.KeycloakRealm;

    /** Keycloak's clock and this server's differ by seconds, not minutes. */
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /** The environment's values; null when tokens are off. Throws ArgumentException for an issuer that is no https address. */
    public static TokenSettings? Parse(string? issuer, string? clients, string? audience)
    {
        var iss = string.IsNullOrWhiteSpace(issuer) ? DefaultIssuer : issuer.Trim();
        if (iss.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(iss, UriKind.Absolute, out var uri) || !IsSecure(uri))
            throw new ArgumentException($"DMC_MCP_ISSUER must be the realm's https address (or off), not \"{iss}\"");
        return new TokenSettings(iss, List(clients), List(audience));
    }

    /** https, or plain http on this machine only (a Keycloak in a developer's container). */
    internal static bool IsSecure(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static IReadOnlyList<string> List(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /** For the startup line: tokens from …, clients …, audience …. */
    public string Describe() =>
        $"tokens from {Issuer}, clients {(Clients.Count == 0 ? "any" : string.Join(", ", Clients))}, "
        + $"audience {(Audiences.Count == 0 ? "not checked" : string.Join(", ", Audiences))}";
}

/// <summary>
/// What the door learns from a token: the person (Caller), or why the token is refused (Refusal — fixed text, safe
/// for a header, never the token), or that tokens cannot be checked right now (Unavailable — the keys are missing).
/// </summary>
public sealed record TokenCheck(Caller? Caller, string? Refusal, bool Unavailable)
{
    internal static TokenCheck Refused(string why) => new(null, why, false);
    internal static readonly TokenCheck NoKeys = new(null, null, true);
}

/// <summary>
/// Checks a person's access token. Only a Keycloak access token of the trusted realm passes: signed with one of the
/// realm's published keys by an asymmetric algorithm (alg none and HS* never), iss exactly the realm, unexpired and
/// already valid (30 seconds of skew), payload typ Bearer (an ID, refresh or offline token is not for calling an
/// API), a subject, and — when the lists are set — azp and aud on them. Nothing here reaches the network per
/// token: the keys come from <see cref="IssuerKeys"/>.
/// </summary>
public sealed class UserTokens(TokenSettings settings, IssuerKeys keys)
{
    /** RFC 7518's asymmetric signatures that .NET verifies; HS* would let a forger sign with any secret it picks. */
    internal static readonly string[] Algorithms =
        { "RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512" };

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    public TokenSettings Settings => settings;

    /** Why the issuer's keys could not be fetched last time, for the log; null when they could. */
    public string? KeysError => keys.LastError;

    /** Fetches the keys ahead of the first token — so the startup log can say whether Keycloak answers. */
    public Task<IReadOnlyList<Microsoft.IdentityModel.Tokens.SecurityKey>> WarmUp(CancellationToken ct = default) => keys.Current(ct);

    public async Task<TokenCheck> Check(string token, CancellationToken ct = default)
    {
        JsonWebToken parsed;
        try { parsed = new JsonWebToken(token); }
        catch (Exception) { return TokenCheck.Refused("the token is malformed"); }

        IReadOnlyList<SecurityKey> signing;
        try
        {
            signing = await keys.Current(ct);
            // A key id the realm has not published (yet): it may have just rotated. Fetched again at most once a minute.
            if (!string.IsNullOrEmpty(parsed.Kid) && !signing.Any(k => k.KeyId == parsed.Kid))
                signing = await keys.AfterUnknownKey(ct);
        }
        catch (IssuerKeys.UnavailableException) { return TokenCheck.NoKeys; }

        TokenValidationResult result;
        try { result = await Handler.ValidateTokenAsync(token, Parameters(signing)); }
        catch (Exception e) { return TokenCheck.Refused(Why(e)); }
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt) return TokenCheck.Refused(Why(result.Exception));

        // Keycloak writes the token's kind into the payload: Bearer for an access token, ID, Refresh, Offline.
        if (!string.Equals(Claim(jwt, "typ"), "Bearer", StringComparison.OrdinalIgnoreCase))
            return TokenCheck.Refused("only access tokens are accepted, not ID or refresh tokens");
        var subject = Claim(jwt, "sub");
        if (string.IsNullOrEmpty(subject)) return TokenCheck.Refused("the token names no subject");
        if (settings.Clients.Count > 0 && !settings.Clients.Contains(Claim(jwt, "azp") ?? "", StringComparer.Ordinal))
            return TokenCheck.Refused("the token was issued to a client this server does not accept");

        var name = Claim(jwt, "preferred_username") ?? Claim(jwt, "email") ?? subject;
        return new TokenCheck(Caller.Person(token, name, settings.Issuer, subject), null, false);
    }

    private TokenValidationParameters Parameters(IReadOnlyList<SecurityKey> signing) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = settings.Issuer,
        ValidateAudience = settings.Audiences.Count > 0,
        ValidAudiences = settings.Audiences,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TokenSettings.ClockSkew,
        RequireSignedTokens = true,
        ValidAlgorithms = Algorithms,
        IssuerSigningKeys = signing,
        // The keys come from the issuer's own key set over https; there is no certificate chain of ours to check.
        ValidateIssuerSigningKey = false,
        // The refusal is ours to report, by its reason; the library's own event log stays out of it.
        LogValidationExceptions = false,
    };

    private static string? Claim(JsonWebToken jwt, string name) =>
        jwt.TryGetPayloadValue<string>(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /** The reason in a few plain words: it goes into a WWW-Authenticate header, so ASCII, no quotes, no token. */
    private static string Why(Exception? e) => e switch
    {
        SecurityTokenExpiredException => "the token has expired",
        SecurityTokenNotYetValidException => "the token is not valid yet",
        SecurityTokenNoExpirationException => "the token has no expiry",
        SecurityTokenInvalidLifetimeException => "the token's lifetime is not valid",
        SecurityTokenInvalidIssuerException => "the token is from another issuer",
        SecurityTokenInvalidAudienceException => "the token is not meant for this server (audience)",
        SecurityTokenInvalidAlgorithmException => "the token's signing algorithm is not accepted",
        SecurityTokenSignatureKeyNotFoundException => "the token's signature is not by a key of the issuer",
        SecurityTokenInvalidSignatureException => "the token's signature does not check out",
        SecurityTokenMalformedException or ArgumentException => "the token is malformed",
        _ => "the token is not valid",
    };
}
