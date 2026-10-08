using DmcMcp;
using Xunit;

/**
 * The realm's keys while Keycloak is away. Before they were ever loaded, an outage costs Keycloak one attempt a
 * minute, and tokens that arrive meanwhile are answered at once (503 at the door) rather than each waiting out an
 * attempt of its own. Once loaded, the keys in hand keep checking tokens while a refresh runs — or hangs — behind them.
 */
public class IssuerKeysTests
{
    private readonly TestIssuer _realm = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    private IssuerKeys Keys() => new(TestIssuer.Issuer, _realm, _clock);

    private static UserTokens Tokens(IssuerKeys keys) =>
        new(new TokenSettings(TestIssuer.Issuer, new[] { "hermes" }, Array.Empty<string>()), keys);

    [Fact]
    public async Task WhileTheRealmIsDownItIsAskedOnceAMinute()
    {
        _realm.Down = true;
        var keys = Keys();
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<IssuerKeys.UnavailableException>(() => keys.Current());
        Assert.Equal(1, _realm.Attempts);

        _clock.Now += IssuerKeys.AtMostEvery + TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<IssuerKeys.UnavailableException>(() => keys.Current());
        Assert.Equal(2, _realm.Attempts);

        _realm.Down = false; // back: the next minute's attempt brings the keys
        _clock.Now += IssuerKeys.AtMostEvery + TimeSpan.FromSeconds(1);
        Assert.NotEmpty(await keys.Current());
    }

    /** As the door sees it: every token of the minute is "unavailable" (503), and Keycloak is asked once. */
    [Fact]
    public async Task TokensDuringAnOutageAreAnsweredWithoutAskingAgain()
    {
        _realm.Down = true;
        var tokens = Tokens(Keys());
        for (var i = 0; i < 5; i++) Assert.True((await tokens.Check(_realm.Token())).Unavailable);
        Assert.Equal(1, _realm.Attempts);
    }

    /**
     * Tokens that arrive while the first attempt hangs wait for that one attempt and are answered together when it
     * fails — not one after another, each with an attempt (and a timeout) of its own.
     */
    [Fact(Timeout = 20000)]
    public async Task TokensThatArriveTogetherShareOneAttempt()
    {
        _realm.Down = true;
        _realm.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = Keys();
        var waiting = Enumerable.Range(0, 5).Select(_ => keys.Current()).ToList();
        _realm.Hold.SetResult();
        foreach (var w in waiting) await Assert.ThrowsAsync<IssuerKeys.UnavailableException>(() => w);
        Assert.Equal(1, _realm.Attempts);
    }

    /** Due for the 12-hour refresh and Keycloak hangs: tokens are checked at once with the keys in hand. */
    [Fact(Timeout = 20000)]
    public async Task ARefreshThatHangsKeepsNoTokenWaiting()
    {
        var keys = Keys();
        var loaded = await keys.Current();
        _clock.Now += IssuerKeys.KeepFor + TimeSpan.FromSeconds(1);
        _realm.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 3; i++)
            Assert.Same(loaded, await keys.Current().WaitAsync(TimeSpan.FromSeconds(5)));
        _realm.Hold.SetResult();
        await keys.Settled;
        Assert.Equal(2, _realm.DiscoveryFetches); // one refresh for the three, not one each
        Assert.Equal(2, _realm.JwksFetches);
    }

    /** A refresh that fails keeps the keys in hand and is tried again a minute later, not on every token. */
    [Fact]
    public async Task AFailedRefreshIsTriedAgainAMinuteLater()
    {
        var keys = Keys();
        var loaded = await keys.Current();
        _clock.Now += IssuerKeys.KeepFor + TimeSpan.FromSeconds(1);
        _realm.Down = true;
        var before = _realm.Attempts;
        for (var i = 0; i < 3; i++)
        {
            Assert.Same(loaded, await keys.Current());
            await keys.Settled;
        }
        Assert.Equal(before + 1, _realm.Attempts);

        _realm.Down = false;
        _clock.Now += IssuerKeys.AtMostEvery + TimeSpan.FromSeconds(1);
        await keys.Current();
        await keys.Settled;
        Assert.Equal(2, _realm.JwksFetches);
    }

    /** A token signed by a key the realm has just rotated in waits for the refresh already under way, not for one of its own. */
    [Fact(Timeout = 20000)]
    public async Task ANewKeyWaitsForTheRefreshUnderWay()
    {
        var keys = Keys();
        var tokens = Tokens(keys);
        Assert.NotNull((await tokens.Check(_realm.Token())).Caller);
        _clock.Now += IssuerKeys.KeepFor + TimeSpan.FromSeconds(1);
        _realm.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await keys.Current().WaitAsync(TimeSpan.FromSeconds(5)); // the refresh starts, and hangs
        _realm.Rotate(dropOld: true);
        var check = tokens.Check(_realm.Token());
        _realm.Hold.SetResult();
        Assert.NotNull((await check.WaitAsync(TimeSpan.FromSeconds(10))).Caller);
        Assert.Equal(2, _realm.JwksFetches);
    }
}
