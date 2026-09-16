using DmcMcp;
using Xunit;

/**
 * The parts of the browser sign-in that can be wrong silently: a challenge the server would reject,
 * an authorize URL missing a parameter, and the reading of whatever comes back on the redirect.
 * The socket round-trip itself is left to the live flow.
 */
public class BrowserLoginTests
{
    [Fact]
    public void ChallengeIsTheS256OfTheVerifierInBase64Url()
    {
        // The one vector in RFC 7636 appendix B — if this drifts, every sign-in fails at the server.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", BrowserLogin.ChallengeFor(verifier));
    }

    [Fact]
    public void VerifiersAreLongEnoughAndNotReused()
    {
        var a = BrowserLogin.NewVerifier();
        var b = BrowserLogin.NewVerifier();
        Assert.NotEqual(a, b);
        // RFC 7636: 43..128 characters, and nothing that needs escaping in a URL.
        Assert.InRange(a.Length, 43, 128);
        Assert.DoesNotContain('=', a);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }

    [Fact]
    public void TheAuthorizeEndpointIsTheTokenEndpointsSibling()
    {
        Assert.Equal("https://host/realms/licsys/protocol/openid-connect/auth",
            BrowserLogin.AuthorizeEndpointFor("https://host/realms/licsys/protocol/openid-connect/token"));
        // Something that is not a token endpoint is left alone rather than mangled.
        Assert.Equal("https://host/weird", BrowserLogin.AuthorizeEndpointFor("https://host/weird"));
    }

    [Fact]
    public void TheAuthorizeUrlCarriesEverythingTheServerNeeds()
    {
        string url = BrowserLogin.AuthorizeUrl("https://host/auth", "extension-store",
            "http://localhost:5123/callback", "the-state", "the-challenge");

        Assert.StartsWith("https://host/auth?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=extension-store", url);
        Assert.Contains("code_challenge=the-challenge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=the-state", url);
        // offline_access is what keeps the stored refresh token usable later on.
        Assert.Contains("offline_access", url);
        // The redirect must survive escaping — a raw ':' or '/' here is what breaks the round-trip.
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5123%2Fcallback", url);
    }

    [Fact]
    public void TheCallbackIsReadForCodeStateAndErrors()
    {
        var ok = BrowserLogin.Read(new Uri("http://localhost:1/callback?code=abc&state=xyz"));
        Assert.Equal("abc", ok.Code);
        Assert.Equal("xyz", ok.State);
        Assert.Null(ok.Error);

        var denied = BrowserLogin.Read(new Uri(
            "http://localhost:1/callback?error=invalid_request&error_description=Invalid+redirect+uri"));
        Assert.Null(denied.Code);
        // The description is the half that says WHAT to fix, so it has to survive into the message.
        Assert.Equal("invalid_request: Invalid redirect uri", denied.Error);

        Assert.Equal("no request url", BrowserLogin.Read(null).Error);
    }

    [Fact]
    public void AFreeLoopbackPortIsUsable()
    {
        int port = BrowserLogin.FreeLoopbackPort();
        Assert.InRange(port, 1024, 65535);
        Assert.NotEqual(port, 0);
    }
}
