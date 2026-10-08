using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace DmcMcp;

/// <summary>
/// The trusted issuer's signing keys, found the way OIDC prescribes: &lt;issuer&gt;/.well-known/openid-configuration
/// names the jwks_uri, and that names the keys. Fetched when the first token arrives and kept 12 hours, so no request
/// waits on Keycloak after that: when they are due, the keys in hand go on checking tokens while the refresh runs in
/// the background (they are the realm's until it publishes others). A token signed with a key that is not among them
/// (the realm rotated its key) makes them be fetched again — at most once a minute, so tokens with invented key ids
/// cannot make the server hammer Keycloak. One fetch at a time: whoever needs keys while one is under way waits for
/// that one. After a failed fetch Keycloak is not asked again for a minute; until the keys were ever loaded, tokens in
/// that minute are answered at once (<see cref="UnavailableException"/>, a 503 at the door).
/// </summary>
public class IssuerKeys
{
    internal static readonly TimeSpan KeepFor = TimeSpan.FromHours(12);
    internal static readonly TimeSpan AtMostEvery = TimeSpan.FromMinutes(1);
    /** A discovery document or a key set is a few kilobytes; nothing larger is read. */
    private const int MaxDocumentBytes = 1024 * 1024;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _issuer;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private volatile Snapshot? _keys;
    /** The fetch under way, if any — whoever needs one meanwhile waits for this one; it gives null when it failed. */
    private Task<Snapshot?>? _fetch;
    /** No new attempt before this, after one failed. */
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    /** When a key id nobody knew last made the keys be fetched again. */
    private DateTimeOffset _lastRefetch = DateTimeOffset.MinValue;

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, string JwksUri, DateTimeOffset FetchedAt);

    /** Keys are unknown and cannot be fetched: tokens cannot be checked at all for now. */
    public sealed class UnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    public IssuerKeys(string issuer, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _issuer = issuer;
        _http = handler == null ? Shared : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? TimeProvider.System;
    }

    public string Issuer => _issuer;

    /** Why the last fetch failed — for the log line; null when it did not. */
    public string? LastError { get; private set; }

    /** The fetch under way, or a finished task when there is none: tests wait out a background refresh with it. */
    internal Task Settled
    {
        get { lock (_lock) return _fetch ?? Task.CompletedTask; }
    }

    /** The keys to check a token with. Throws <see cref="UnavailableException"/> when there are none to be had. */
    public async Task<IReadOnlyList<SecurityKey>> Current(CancellationToken ct = default)
    {
        if (_keys is { } snap)
        {
            if (_clock.GetUtcNow() - snap.FetchedAt >= KeepFor) RefreshBehind();
            return snap.Keys;
        }
        Task<Snapshot?> fetch;
        lock (_lock)
        {
            if (_keys is { } meanwhile) return meanwhile.Keys;
            // The last attempt failed less than a minute ago: answer now, and leave Keycloak alone.
            if (_fetch == null && _clock.GetUtcNow() < _retryAfter) throw Unavailable();
            fetch = _fetch ?? Start(discover: true);
        }
        return (await fetch.WaitAsync(ct))?.Keys ?? throw Unavailable();
    }

    /**
     * A token names a key that is not among the current ones. The realm may have rotated its key: the key set is
     * fetched again — or the fetch under way is waited for — unless one was made, or failed, in the last minute.
     * Returns the keys to check the token with.
     */
    public async Task<IReadOnlyList<SecurityKey>> AfterUnknownKey(CancellationToken ct = default)
    {
        Snapshot snap;
        Task<Snapshot?> fetch;
        lock (_lock)
        {
            snap = _keys ?? throw new UnavailableException($"the keys of {_issuer} are not loaded");
            if (_fetch != null) fetch = _fetch;
            else
            {
                var now = _clock.GetUtcNow();
                if (now - _lastRefetch < AtMostEvery || now < _retryAfter) return snap.Keys;
                _lastRefetch = now;
                fetch = Start(discover: false);
            }
        }
        return ((await fetch.WaitAsync(ct)) ?? _keys ?? snap).Keys;
    }

    /** The 12-hourly refresh, started and not waited for — unless one is under way or failed within the minute. */
    private void RefreshBehind()
    {
        lock (_lock)
        {
            if (_fetch == null && _clock.GetUtcNow() >= _retryAfter) Start(discover: true);
        }
    }

    /**
     * Under the lock: the one fetch, off the caller's thread, so it cannot finish before it is recorded as under way.
     * It records its own outcome — the keys, or the error and a minute's pause — and is never cancelled by a caller
     * that stops waiting: others may be waiting for it too. The HttpClient's 15 seconds bound it.
     */
    private Task<Snapshot?> Start(bool discover)
    {
        var jwksUri = discover ? null : _keys?.JwksUri;
        return _fetch = Task.Run(async () =>
        {
            try
            {
                var snap = await Fetch(jwksUri, discover: jwksUri == null, CancellationToken.None);
                lock (_lock) { _keys = snap; LastError = null; _fetch = null; }
                return snap;
            }
            catch (Exception e)
            {
                lock (_lock) { LastError = e.Message; _retryAfter = _clock.GetUtcNow() + AtMostEvery; _fetch = null; }
                return null;
            }
        });
    }

    private UnavailableException Unavailable() =>
        new($"the keys of {_issuer} could not be fetched: {LastError ?? "no answer"}");

    private async Task<Snapshot> Fetch(string? jwksUri, bool discover, CancellationToken ct)
    {
        if (discover || jwksUri == null)
        {
            using var doc = JsonDocument.Parse(await Get(_issuer.TrimEnd('/') + "/.well-known/openid-configuration", ct));
            var root = doc.RootElement;
            var named = Str(root, "issuer");
            // OIDC Discovery: the document is the issuer's only if it names that very issuer.
            if (!string.Equals(named, _issuer, StringComparison.Ordinal))
                throw new InvalidDataException($"discovery names the issuer \"{named}\", not {_issuer}");
            jwksUri = Str(root, "jwks_uri");
            if (jwksUri == null || !Uri.TryCreate(jwksUri, UriKind.Absolute, out var uri) || !TokenSettings.IsSecure(uri))
                throw new InvalidDataException("discovery gives no https jwks_uri");
        }
        var keys = new JsonWebKeySet(await Get(jwksUri, ct)).GetSigningKeys();
        if (keys.Count == 0) throw new InvalidDataException("the issuer publishes no signing keys");
        return new Snapshot(keys.ToList(), jwksUri, _clock.GetUtcNow());
    }

    private async Task<string> Get(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{url} answered {(int)resp.StatusCode}");
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxDocumentBytes) throw new InvalidDataException($"{url} is larger than 1 MB");
            buffer.Write(chunk, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
