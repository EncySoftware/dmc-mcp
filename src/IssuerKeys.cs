using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace DmcMcp;

/// <summary>
/// The trusted issuer's signing keys, found the way OIDC prescribes: &lt;issuer&gt;/.well-known/openid-configuration
/// names the jwks_uri, and that names the keys. Fetched when the first token arrives and kept 12 hours, so no request
/// waits on Keycloak after that; a refresh that fails keeps the keys in hand and is tried again a minute later. A
/// token signed with a key that is not among them (the realm rotated its key) makes them be fetched again — at most
/// once a minute, so tokens with invented key ids cannot make the server hammer Keycloak.
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
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private volatile Snapshot? _keys;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
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

    /** The keys to check a token with. Throws <see cref="UnavailableException"/> when there are none to be had. */
    public async Task<IReadOnlyList<SecurityKey>> Current(CancellationToken ct = default)
    {
        if (Fresh(_keys) is { } keys) return keys;
        await _fetching.WaitAsync(ct);
        try
        {
            var snap = _keys;
            if (Fresh(snap) is { } fetchedMeanwhile) return fetchedMeanwhile;
            try
            {
                _keys = snap = await Fetch(snap?.JwksUri, discover: true, ct);
                LastError = null;
                return snap.Keys;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                LastError = e.Message;
                _retryAfter = _clock.GetUtcNow() + AtMostEvery;
                if (snap != null) return snap.Keys; // Keycloak is down for a moment: the keys in hand still hold
                throw new UnavailableException($"the keys of {_issuer} could not be fetched: {e.Message}", e);
            }
        }
        finally { _fetching.Release(); }
    }

    /**
     * A token names a key that is not among the current ones. The realm may have rotated its key: the key set is
     * fetched again, unless that was already done in the last minute. Returns the keys to check the token with.
     */
    public async Task<IReadOnlyList<SecurityKey>> AfterUnknownKey(CancellationToken ct = default)
    {
        await _fetching.WaitAsync(ct);
        try
        {
            var snap = _keys;
            if (snap == null) throw new UnavailableException($"the keys of {_issuer} are not loaded");
            var now = _clock.GetUtcNow();
            if (now - _lastRefetch < AtMostEvery) return snap.Keys;
            _lastRefetch = now;
            try
            {
                _keys = snap = await Fetch(snap.JwksUri, discover: false, ct);
                LastError = null;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                LastError = e.Message;
            }
            return snap.Keys;
        }
        finally { _fetching.Release(); }
    }

    private IReadOnlyList<SecurityKey>? Fresh(Snapshot? snap)
    {
        if (snap == null) return null;
        var now = _clock.GetUtcNow();
        return now - snap.FetchedAt < KeepFor || now < _retryAfter ? snap.Keys : null;
    }

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
