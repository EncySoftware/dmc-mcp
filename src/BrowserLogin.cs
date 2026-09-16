using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DmcMcp;

/**
 * Sign in through the СПРУТКАМ sign-in page in a browser (OAuth authorization code + PKCE, loopback
 * redirect) instead of typing an email and password into this terminal.
 *
 * <para>Three reasons it is the better door. The tool never sees the password. Whatever the account
 * needs — SSO, two-factor, a password manager — happens in the browser where it works, and a
 * password grant simply breaks the day two-factor is switched on. And it needs no Direct Access
 * Grants on the Keycloak client — which matters here, because the store has no Keycloak client of
 * its own in the СПРУТКАМ realm and this tool borrows `dealer-space`.</para>
 *
 * <para>What it needs from Keycloak: Standard Flow enabled and <c>http://127.0.0.1:*∕callback</c>
 * among the client's valid redirect URIs. Until that is in place the page refuses with "Invalid
 * redirect uri", so {@code login --password} keeps the old console flow available.</para>
 */
public static class BrowserLogin
{
    /** Enough for a human to find the window, log in, and deal with a two-factor prompt. */
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(3);

    /// <summary>PKCE verifier: 32 random bytes, base64url — the high end of what the RFC allows.</summary>
    public static string NewVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    /// <summary>S256 challenge for a verifier. Plain is never used — servers may reject it.</summary>
    public static string ChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>The token endpoint's sibling. Keycloak's paths differ only in the last segment.</summary>
    public static string AuthorizeEndpointFor(string tokenEndpoint) =>
        tokenEndpoint.EndsWith("/token", StringComparison.Ordinal)
            ? tokenEndpoint[..^"/token".Length] + "/auth"
            : tokenEndpoint;

    public static string AuthorizeUrl(string authorizeEndpoint, string clientId, string redirectUri,
                                     string state, string challenge)
    {
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            // offline_access is what makes the stored refresh token outlive the session, exactly as
            // the console flow relied on.
            ["scope"] = "openid offline_access",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return authorizeEndpoint + "?" + string.Join("&",
            q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>A loopback port nobody is using. Asking for port 0 lets the OS pick.</summary>
    public static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /** What came back on the loopback redirect. */
    public record Callback(string? Code, string? State, string? Error);

    /// <summary>What the browser tab says once it has served its purpose.</summary>
    private const string DonePage =
        "<!doctype html><meta charset=utf-8><title>СПРУТКАМ store</title>"
        + "<body style=\"font:16px system-ui;padding:3rem;text-align:center\">"
        + "<p>Signed in to the СПРУТКАМ Extension Store.</p>"
        + "<p style=\"color:#666\">You can close this tab and go back to the terminal.</p>";

    /**
     * Runs the whole flow and returns the refresh token, or null with the reason written out.
     * Deliberately one method: every step shares the verifier, the state and the port, and pulling
     * them apart would only make room for them to disagree.
     */
    public static async Task<string?> SignIn(string tokenEndpoint, string clientId,
                                            Func<string, Task> openBrowser, Action<string> write,
                                            HttpClient http)
    {
        string verifier = NewVerifier(), state = NewVerifier();
        int port = FreeLoopbackPort();

        // "localhost" first: on Windows HttpListener may bind it without elevation, while a literal
        // 127.0.0.1 prefix often needs a netsh URL reservation. Whichever binds decides the
        // redirect_uri, because it has to be the exact string sent to the authorize endpoint.
        using var listener = new HttpListener();
        string? redirectUri = null;
        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://{host}:{port}/callback/");
            try { listener.Start(); redirectUri = $"http://{host}:{port}/callback"; break; }
            catch (HttpListenerException) { /* try the other spelling */ }
        }
        if (redirectUri == null)
        {
            write($"Could not listen on port {port} — run `sprutcam-extension-mcp login --password` instead.");
            return null;
        }

        string url = AuthorizeUrl(AuthorizeEndpointFor(tokenEndpoint), clientId, redirectUri,
                                  state, ChallengeFor(verifier));
        // Plain ASCII on purpose: a Windows console in codepage 866 turns a nice ellipsis into mojibake.
        write("Opening the СПРУТКАМ sign-in page in your browser...");
        write("If it does not open, paste this address yourself:");
        write("  " + url);
        await openBrowser(url);

        var context = await WaitForCallback(listener, write);
        if (context == null) return null;

        var cb = Read(context.Request.Url);
        await Respond(context, DonePage);

        if (cb.Error != null) { write("The sign-in page said: " + cb.Error); return null; }
        if (cb.Code == null) { write("The sign-in page came back without a code."); return null; }
        if (cb.State != state) { write("The reply did not match this request — sign in again."); return null; }

        var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = cb.Code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }));
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            write("Could not exchange the code for a token: " + Trim(json));
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        string? refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
        {
            write("The server returned no refresh token — cannot stay signed in.");
            return null;
        }
        return refresh;
    }

    /** The browser may never come back — a refused redirect_uri looks exactly like silence here. */
    private static async Task<HttpListenerContext?> WaitForCallback(HttpListener listener, Action<string> write)
    {
        var incoming = listener.GetContextAsync();
        if (await Task.WhenAny(incoming, Task.Delay(Wait)) != incoming)
        {
            write("Gave up waiting for the browser.");
            write("If the page refused the address as an invalid redirect URI, the store's Keycloak "
                  + "client does not allow http://127.0.0.1 yet — run `sprutcam-extension-mcp login "
                  + "--password` for now and ask the store team to allow it.");
            return null;
        }
        return await incoming;
    }

    /** Extracted so the query parsing can be tested without a socket. */
    public static Callback Read(Uri? requestUrl)
    {
        if (requestUrl == null) return new Callback(null, null, "no request url");
        var q = ParseQuery(requestUrl.Query);
        string? error = q.GetValueOrDefault("error");
        if (error != null && q.GetValueOrDefault("error_description") is { Length: > 0 } d)
            error += ": " + d;
        return new Callback(q.GetValueOrDefault("code"), q.GetValueOrDefault("state"), error);
    }

    /** Six lines instead of a dependency on System.Web for one call. */
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "" : pair[(eq + 1)..];
            map[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return map;
    }

    private static async Task Respond(HttpListenerContext context, string html)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
