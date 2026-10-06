using DmcMcp;
using Xunit;

/** The hosted server's log says once at startup whom it acts as — the only check its operator needs. */
public class SignInKeepAliveTests
{
    [Fact]
    public async Task NotSignedInPointsAtTheCommand()
    {
        var line = await SignInKeepAlive.Describe(new FakeDmcClient(), new FakeTokens(null));
        Assert.Contains("NOT signed in", line);
        Assert.Contains(SignInKeepAlive.HostedLoginCommand, line);
    }

    [Fact]
    public async Task PublisherIsReported()
    {
        var client = new FakeDmcClient { Who = new MeInfo("hermes@encycam.com", new[] { "DEALER" }, null, null) };
        var line = await SignInKeepAlive.Describe(client, new FakeTokens("tok"));
        Assert.Contains("hermes@encycam.com", line);
        Assert.DoesNotContain("WARNING", line);
    }

    [Fact]
    public async Task MissingPublisherRoleIsAWarning()
    {
        var client = new FakeDmcClient { Who = new MeInfo("hermes@encycam.com", new[] { "USER" }, null, null) };
        var line = await SignInKeepAlive.Describe(client, new FakeTokens("tok"));
        Assert.Contains("WARNING", line);
        Assert.Contains("Publisher", line);
    }

    /**
     * Keycloak unreachable, DNS not up yet after a reboot, a 20-second timeout: a line in the log, not an
     * exception — out of a BackgroundService one stops the whole host, uploads and inspect_archive included.
     */
    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("json")]
    public async Task KeycloakFailuresAreALineNotACrash(string kind)
    {
        Exception e = kind switch
        {
            "http" => new HttpRequestException("Connection refused (127.0.0.1:1)"),
            "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            _ => new System.Text.Json.JsonException("'<' is an invalid start of a value"),
        };
        var line = await SignInKeepAlive.Describe(new FakeDmcClient(), new Tokens(() => throw e));
        Assert.Contains("could not be checked", line);
    }

    /**
     * The operator signs in after the container has started: the log says so within a minute, not after the next
     * 12-hour tick — then the check goes back to every 12 hours, and an unchanged line is not repeated.
     */
    [Fact]
    public async Task UntilSignedInItChecksEveryMinute()
    {
        var answers = new Queue<string?>(new[] { null, "tok", "tok" });
        var (delays, lines) = await Run(new FakeDmcClient(), new Tokens(() => answers.Dequeue()), checks: 3);
        Assert.Equal(new[] { SignInKeepAlive.Retry, SignInKeepAlive.Every, SignInKeepAlive.Every }, delays);
        Assert.Equal(2, lines.Count);
        Assert.Contains("NOT signed in", lines[0]);
        Assert.Contains("Signed in to DMC as tester", lines[1]);
    }

    [Fact]
    public async Task AKeycloakOutageIsRetriedAndTheServiceLives()
    {
        var calls = 0;
        var (delays, lines) = await Run(new FakeDmcClient(),
            new Tokens(() => ++calls == 1 ? throw new HttpRequestException("Name or service not known") : "tok"), checks: 2);
        Assert.Equal(new[] { SignInKeepAlive.Retry, SignInKeepAlive.Every }, delays);
        Assert.Contains("could not be checked", lines[0]);
        Assert.Contains("Signed in to DMC as", lines[1]);
    }

    /** Runs the service for a number of checks: what it waited between them and what it logged. */
    private static async Task<(List<TimeSpan> Delays, List<string> Lines)> Run(IDmcClient dmc, DmcTokenProvider tokens, int checks)
    {
        var delays = new List<TimeSpan>();
        var log = new ListLogger();
        var service = new SignInKeepAlive(dmc, tokens, log)
        {
            Delay = (t, _) =>
            {
                delays.Add(t);
                return delays.Count == checks ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask;
            },
        };
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        return (delays, log.Lines);
    }

    private sealed class Tokens(Func<string?> next) : DmcTokenProvider
    {
        public override Task<string?> GetAccessToken() => Task.FromResult(next());
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<SignInKeepAlive>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? e, Func<TState, Exception?, string> format) => Lines.Add(format(state, e));
    }
}
