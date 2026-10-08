using System.Security.Cryptography;
using DmcMcp;
using Xunit;

/**
 * The second way into the hosted server: a person's own access token from the trusted Keycloak realm. Only a real
 * access token of that realm, signed by its published keys and still valid, lets a call act as that person.
 */
public class UserTokensTests
{
    private readonly TestIssuer _realm = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    private UserTokens Tokens(string[]? clients = null, string[]? audiences = null) =>
        new(new TokenSettings(TestIssuer.Issuer, clients ?? Array.Empty<string>(), audiences ?? Array.Empty<string>()),
            new IssuerKeys(TestIssuer.Issuer, _realm, _clock));

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static void Refused(TokenCheck r, string token, string? why = null)
    {
        Assert.Null(r.Caller);
        Assert.False(r.Unavailable);
        Assert.NotNull(r.Refusal);
        if (token.Length > 0) Assert.DoesNotContain(token, r.Refusal);
        if (why != null) Assert.Contains(why, r.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAccessTokenOfTheRealmActsAsItsPerson()
    {
        var token = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var r = await Tokens().Check(token);
        Assert.NotNull(r.Caller);
        Assert.True(r.Caller!.IsUser);
        Assert.Equal(token, r.Caller.AccessToken);
        Assert.Equal("anna@example.com", r.Caller.Name);
        Assert.Null(r.Refusal);
    }

    /** Uploads are owned by the subject: the same person with a fresh token owns the same uploads, another does not. */
    [Fact]
    public async Task TheOwnerIsThePersonNotTheToken()
    {
        var tokens = Tokens();
        var a1 = (await tokens.Check(_realm.Token(sub: "sub-anna"))).Caller!;
        var a2 = (await tokens.Check(_realm.Token(sub: "sub-anna"))).Caller!;
        var b = (await tokens.Check(_realm.Token(sub: "sub-boris", name: "boris@example.com"))).Caller!;
        Assert.Equal(a1.Owner, a2.Owner);
        Assert.NotEqual(a1.Owner, b.Owner);
        Assert.NotEqual(Caller.Server.Owner, a1.Owner);
        Assert.DoesNotContain("sub-anna", a1.Owner); // a hash on the disk, not the person's id
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        var token = _realm.Token(edit: c => c["exp"] = Now - 120);
        Refused(await Tokens().Check(token), token, "expired");
    }

    /** Clocks differ by a few seconds between Keycloak and this server: a token just past exp still counts. */
    [Fact]
    public async Task ASmallClockSkewIsForgiven()
    {
        var r = await Tokens().Check(_realm.Token(edit: c => c["exp"] = Now - 10));
        Assert.NotNull(r.Caller);
    }

    [Fact]
    public async Task ATokenNotValidYetIsRefused()
    {
        var token = _realm.Token(edit: c => { c["nbf"] = Now + 600; c["exp"] = Now + 1200; });
        Refused(await Tokens().Check(token), token, "not valid yet");
    }

    [Fact]
    public async Task ATokenWithoutExpiryIsRefused()
    {
        var token = _realm.Token(edit: c => c["exp"] = null);
        Refused(await Tokens().Check(token), token);
    }

    /** The same keys, another issuer: a token is this realm's only if it says so exactly. */
    [Theory]
    [InlineData("https://evil.test/keycloak/realms/licsys")]
    [InlineData(TestIssuer.Issuer + "/")]
    [InlineData("https://keycloak.test/keycloak/realms/master")]
    public async Task AnotherIssuerIsRefused(string iss)
    {
        var token = _realm.Token(edit: c => c["iss"] = iss);
        Refused(await Tokens().Check(token), token, "issuer");
    }

    [Fact]
    public async Task WithoutAllowListsAnyClientAndAudienceOfTheRealmIsAccepted()
    {
        var r = await Tokens().Check(_realm.Token(edit: c => { c["azp"] = "some-other-client"; c["aud"] = null; }));
        Assert.NotNull(r.Caller);
    }

    [Fact]
    public async Task AClientOnTheListIsAccepted() =>
        Assert.NotNull((await Tokens(clients: new[] { "hermes", "hermes-dev" }).Check(_realm.Token())).Caller);

    [Theory]
    [InlineData("dealer-space")]
    [InlineData(null)]
    public async Task AClientOffTheListIsRefused(string? azp)
    {
        var token = _realm.Token(edit: c => c["azp"] = azp);
        Refused(await Tokens(clients: new[] { "hermes" }).Check(token), token, "client");
    }

    [Fact]
    public async Task AnAudienceOnTheListIsAccepted()
    {
        var token = _realm.Token(edit: c => c["aud"] = new[] { "account", "dmc-mcp" });
        Assert.NotNull((await Tokens(audiences: new[] { "dmc-mcp" }).Check(token)).Caller);
    }

    [Fact]
    public async Task AnAudienceOffTheListIsRefused()
    {
        var token = _realm.Token(edit: c => c["aud"] = "account");
        Refused(await Tokens(audiences: new[] { "dmc-mcp" }).Check(token), token, "audience");
    }

    [Fact]
    public async Task AlgNoneIsRefused()
    {
        var token = _realm.Unsigned();
        Refused(await Tokens().Check(token), token);
    }

    /** HS256 with the realm's own public key as the secret — the key-confusion forgery. */
    [Fact]
    public async Task Hs256IsRefused()
    {
        var secret = _realm.Current.Key.ExportSubjectPublicKeyInfo();
        var token = _realm.Hs256(secret);
        Refused(await Tokens().Check(token), token);
    }

    /** Keycloak's ID token and its refresh and offline tokens are signed JWTs too, but not for calling an API. */
    [Theory]
    [InlineData("ID")]
    [InlineData("Refresh")]
    [InlineData("Offline")]
    [InlineData(null)]
    public async Task OnlyAccessTokensAreAccepted(string? typ)
    {
        var token = _realm.Token(edit: c => c["typ"] = typ);
        Refused(await Tokens().Check(token), token, "access token");
    }

    [Fact]
    public async Task ATokenWithoutASubjectIsRefused()
    {
        var token = _realm.Token(edit: c => c["sub"] = null);
        Refused(await Tokens().Check(token), token);
    }

    [Theory]
    [InlineData("abc.def.ghi")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln")]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task GarbageIsRefusedNotThrown(string token) => Refused(await Tokens().Check(token), token);

    [Fact]
    public async Task AKeyTheRealmNeverPublishedIsRefused()
    {
        using var stranger = RSA.Create(2048);
        var token = _realm.Token(header: h => h["kid"] = "made-up", signWith: stranger);
        Refused(await Tokens().Check(token), token, "signature");
    }

    /** A published kid on a signature made with another key: forged, and no reason to ask Keycloak again. */
    [Fact]
    public async Task AForgedSignatureUnderAKnownKidIsRefused()
    {
        using var stranger = RSA.Create(2048);
        var tokens = Tokens();
        var token = _realm.Token(signWith: stranger);
        Refused(await tokens.Check(token), token, "signature");
        Assert.Equal(1, _realm.JwksFetches);
    }

    /** No request waits on Keycloak once the keys are in: discovery and the keys are read once for many tokens. */
    [Fact]
    public async Task TheKeysAreFetchedOnceForManyTokens()
    {
        var tokens = Tokens();
        for (var i = 0; i < 5; i++) Assert.NotNull((await tokens.Check(_realm.Token(sub: "s" + i))).Caller);
        Assert.Equal(1, _realm.DiscoveryFetches);
        Assert.Equal(1, _realm.JwksFetches);
    }

    [Fact]
    public async Task TheKeysAreReadAgainAfterTwelveHours()
    {
        var tokens = Tokens();
        await tokens.Check(_realm.Token());
        _clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1);
        Assert.NotNull((await tokens.Check(_realm.Token())).Caller);
        Assert.Equal(2, _realm.JwksFetches);
    }

    /** Keycloak rotates the realm's key: the first token signed by the new one makes the server fetch the keys again. */
    [Fact]
    public async Task ARotatedKeyIsPickedUp()
    {
        var tokens = Tokens();
        Assert.NotNull((await tokens.Check(_realm.Token())).Caller);
        _realm.Rotate(dropOld: true);
        Assert.NotNull((await tokens.Check(_realm.Token())).Caller);
        Assert.Equal(2, _realm.JwksFetches);
    }

    /** An invented kid cannot make the server hammer Keycloak: one fetch a minute at most. */
    [Fact]
    public async Task AnUnknownKidRefetchesAtMostOnceAMinute()
    {
        var tokens = Tokens();
        await tokens.Check(_realm.Token());
        using var stranger = RSA.Create(2048);
        string Forged() => _realm.Token(header: h => h["kid"] = "made-up-" + Guid.NewGuid().ToString("N")[..6], signWith: stranger);
        for (var i = 0; i < 3; i++) Assert.Null((await tokens.Check(Forged())).Caller);
        Assert.Equal(2, _realm.JwksFetches);
        _clock.Now += TimeSpan.FromSeconds(61);
        Assert.Null((await tokens.Check(Forged())).Caller);
        Assert.Equal(3, _realm.JwksFetches);
    }

    /** Keycloak down when the first token arrives: not the token's fault — the caller is told to try again, not refused. */
    [Fact]
    public async Task NoKeysAtAllIsUnavailableNotARefusal()
    {
        _realm.Down = true;
        var r = await Tokens().Check(_realm.Token());
        Assert.True(r.Unavailable);
        Assert.Null(r.Caller);
    }

    /** Keycloak down when the keys are due for a refresh: the keys in hand still check tokens. */
    [Fact]
    public async Task AFailedRefreshKeepsTheKeysInHand()
    {
        var tokens = Tokens();
        await tokens.Check(_realm.Token());
        _clock.Now += TimeSpan.FromHours(13);
        _realm.Down = true;
        Assert.NotNull((await tokens.Check(_realm.Token())).Caller);
    }

    /** Discovery must name the configured issuer: a document for another realm (or a hijacked one) gives no keys. */
    [Fact]
    public async Task DiscoveryNamingAnotherIssuerGivesNoKeys()
    {
        _realm.DiscoveryIssuer = "https://evil.test/keycloak/realms/licsys";
        var r = await Tokens().Check(_realm.Token());
        Assert.True(r.Unavailable);
        Assert.Null(r.Caller);
    }

    [Fact]
    public void TheSettingsReadTheEnvironmentShape()
    {
        var s = TokenSettings.Parse(" https://kc.example/realms/licsys ", " hermes, hermes-dev ,", "");
        Assert.NotNull(s);
        Assert.Equal("https://kc.example/realms/licsys", s!.Issuer);
        Assert.Equal(new[] { "hermes", "hermes-dev" }, s.Clients);
        Assert.Empty(s.Audiences);
        Assert.Equal(TokenSettings.DefaultIssuer, TokenSettings.Parse(null, null, null)!.Issuer);
        Assert.Null(TokenSettings.Parse("off", null, null));
        Assert.Equal("https://webservices.encycam.com/keycloak/realms/licsys", TokenSettings.DefaultIssuer);
    }

    [Theory]
    [InlineData("http://kc.example/realms/licsys")]
    [InlineData("realms/licsys")]
    public void AnIssuerMustBeAnHttpsAddress(string issuer) =>
        Assert.Throws<ArgumentException>(() => TokenSettings.Parse(issuer, null, null));
}
