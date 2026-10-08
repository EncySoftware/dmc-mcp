using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/**
 * A Keycloak realm in memory, served as an HttpMessageHandler: its discovery document, its published keys (JWKS)
 * and access tokens signed by them — the hosted server's token door tested without the network.
 */
public sealed class TestIssuer : HttpMessageHandler
{
    public const string Issuer = "https://keycloak.test/keycloak/realms/licsys";
    public const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";
    public const string JwksUrl = Issuer + "/protocol/openid-connect/certs";

    private readonly List<(string Kid, RSA Key)> _published = new();
    private int _discoveryFetches, _jwksFetches, _attempts;

    public int DiscoveryFetches => _discoveryFetches;
    public int JwksFetches => _jwksFetches;

    /** Every request that reached the realm, answered or not: what an outage costs Keycloak. */
    public int Attempts => _attempts;

    /** Keycloak unreachable: every request fails as a refused connection would. */
    public bool Down { get; set; }

    /** Keycloak that hangs: while set, every request waits for it before it is answered (or fails, when Down). */
    public TaskCompletionSource? Hold { get; set; }

    /** What discovery says its issuer is; null — the real one. */
    public string? DiscoveryIssuer { get; set; }

    public TestIssuer() => Rotate();

    public (string Kid, RSA Key) Current => _published[^1];

    /** A realm key rotation: a new key is published (the old ones stay unless dropped) and signs from now on. */
    public (string Kid, RSA Key) Rotate(bool dropOld = false)
    {
        if (dropOld) _published.Clear();
        var key = (Kid: "kid-" + Guid.NewGuid().ToString("N")[..8], Key: RSA.Create(2048));
        _published.Add(key);
        return key;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        return Answer(request);
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        if (Down) throw new HttpRequestException("Connection refused (keycloak.test:443)");
        var url = request.RequestUri!.ToString();
        if (url == DiscoveryUrl)
        {
            Interlocked.Increment(ref _discoveryFetches);
            return Json(new Dictionary<string, object?>
            {
                ["issuer"] = DiscoveryIssuer ?? Issuer,
                ["jwks_uri"] = JwksUrl,
                ["authorization_endpoint"] = Issuer + "/protocol/openid-connect/auth",
                ["token_endpoint"] = Issuer + "/protocol/openid-connect/token",
            });
        }
        if (url == JwksUrl)
        {
            Interlocked.Increment(ref _jwksFetches);
            var keys = _published.Select(k =>
            {
                var p = k.Key.ExportParameters(false);
                return new Dictionary<string, object?>
                {
                    ["kid"] = k.Kid, ["kty"] = "RSA", ["alg"] = "RS256", ["use"] = "sig",
                    ["n"] = B64(p.Modulus!), ["e"] = B64(p.Exponent!),
                };
            }).ToList();
            // Keycloak publishes an encryption key beside the signing one; it must not count as a signing key.
            keys.Add(new Dictionary<string, object?>
            {
                ["kid"] = "enc-1", ["kty"] = "RSA", ["alg"] = "RSA-OAEP", ["use"] = "enc",
                ["n"] = B64(Current.Key.ExportParameters(false).Modulus!), ["e"] = "AQAB",
            });
            return Json(new Dictionary<string, object?> { ["keys"] = keys });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    /**
     * A Keycloak access token for a person, signed RS256 with the realm's current key. edit changes the payload
     * (a null value removes a claim); header the JOSE header.
     */
    public string Token(string sub = "sub-anna", string name = "anna@example.com",
        Action<Dictionary<string, object?>>? edit = null, Action<Dictionary<string, object?>>? header = null,
        RSA? signWith = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["exp"] = now + 300, ["iat"] = now, ["jti"] = Guid.NewGuid().ToString(), ["iss"] = Issuer,
            ["aud"] = "account", ["sub"] = sub, ["typ"] = "Bearer", ["azp"] = "hermes",
            ["scope"] = "openid profile email", ["email_verified"] = true,
            ["preferred_username"] = name, ["email"] = name,
        };
        edit?.Invoke(claims);
        var h = new Dictionary<string, object?> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = Current.Kid };
        header?.Invoke(h);
        var signingInput = Part(h) + "." + Part(claims);
        var signature = (signWith ?? Current.Key).SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + B64(signature);
    }

    /** The same payload with alg none and no signature — the oldest trick against a careless validator. */
    public string Unsigned(string sub = "sub-anna")
    {
        var t = Token(sub);
        var payload = t.Split('.')[1];
        return Part(new Dictionary<string, object?> { ["alg"] = "none", ["typ"] = "JWT" }) + "." + payload + ".";
    }

    /** HS256 with a secret — what a forger would use if a validator took any algorithm the token names. */
    public string Hs256(byte[] secret, string sub = "sub-anna")
    {
        var t = Token(sub);
        var signingInput = Part(new Dictionary<string, object?> { ["alg"] = "HS256", ["typ"] = "JWT", ["kid"] = Current.Kid })
                           + "." + t.Split('.')[1];
        using var mac = new HMACSHA256(secret);
        return signingInput + "." + B64(mac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
    }

    private static string Part(Dictionary<string, object?> json) =>
        B64(JsonSerializer.SerializeToUtf8Bytes(json.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value)));

    public static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/** A clock the test moves by hand. */
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}
