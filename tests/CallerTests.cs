using DmcMcp;
using Xunit;

/** Who a hosted call runs as lives for one request only, and never shows the person's token. */
public class CallerTests
{
    private static readonly Caller Anna = Caller.Person("tok-anna-secret", "anna@example.com", TestIssuer.Issuer, "sub-anna");

    [Fact]
    public void OutsideARequestThereIsNoCaller() => Assert.Null(Caller.Current);

    [Fact]
    public void TheServersAccountHasNoTokenOfItsOwnHere()
    {
        Assert.False(Caller.Server.IsUser);
        Assert.Null(Caller.Server.AccessToken);
    }

    [Fact]
    public void APersonNeverShowsTheirToken()
    {
        Assert.True(Anna.IsUser);
        Assert.DoesNotContain("tok-anna-secret", Anna.ToString());
        Assert.Contains("anna@example.com", Anna.ToString());
        Assert.DoesNotContain("tok-anna-secret", Anna.TokenHash);
    }

    [Fact]
    public void ACallerIsCurrentForItsRequestOnly()
    {
        using (Caller.Enter(Anna)) Assert.Same(Anna, Caller.Current);
        Assert.Null(Caller.Current);
    }

    /** Work started during a request and still running after it — a timer, a background refresh — does not keep the person. */
    [Fact]
    public async Task WorkThatOutlivesTheRequestDoesNotKeepThePerson()
    {
        var go = new TaskCompletionSource();
        Task<Caller?> later;
        using (Caller.Enter(Anna))
            later = Task.Run(async () => { await go.Task; return Caller.Current; });
        go.SetResult();
        Assert.Null(await later);
    }

    /** Two requests at once, each with its own person: neither sees the other's. */
    [Fact]
    public async Task ConcurrentRequestsKeepTheirOwnCaller()
    {
        var boris = Caller.Person("tok-boris", "boris@example.com", TestIssuer.Issuer, "sub-boris");
        var both = new Barrier(2);
        Caller? Seen(Caller c)
        {
            using (Caller.Enter(c))
            {
                both.SignalAndWait(TimeSpan.FromSeconds(10));
                return Caller.Current;
            }
        }
        var a = Task.Run(() => Seen(Anna));
        var b = Task.Run(() => Seen(boris));
        Assert.Same(Anna, await a);
        Assert.Same(boris, await b);
    }
}
