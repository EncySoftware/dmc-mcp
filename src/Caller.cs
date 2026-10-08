using System.Security.Cryptography;
using System.Text;

namespace DmcMcp;

/// <summary>
/// Who a call on the hosted server runs as. The key lets in the server's own signed-in account
/// (<see cref="Server"/>); a person's access token lets in that person, and every DMC call of the request then
/// carries their token. The door sets the caller for the length of one HTTP request; the tools and the upload
/// endpoint read <see cref="Current"/>. It lives in an AsyncLocal, never in a field of a singleton, and the
/// holder is emptied when the request ends, so work that outlives the request (a background refresh, a timer
/// that captured the context) keeps neither the person nor the token. The token is never shown: ToString and
/// every message use the name, caches use <see cref="TokenHash"/>.
/// </summary>
public sealed class Caller
{
    /** The key's caller: the stored sign-in of the server (or of the machine, in stdio mode). Its uploads' owner is "key". */
    public static Caller Server { get; } = new(null, "the server's own account", "key", "");

    private Caller(string? accessToken, string name, string owner, string tokenHash)
    {
        AccessToken = accessToken;
        Name = name;
        Owner = owner;
        TokenHash = tokenHash;
    }

    /** The person's access token, for DMC calls of this request only; null for the server's own account. */
    public string? AccessToken { get; }

    /** For messages: the token's preferred_username (or email, or subject). */
    public string Name { get; }

    /** Whose uploads: "key", or "user:" and a hash of issuer and subject — the same person with any of their tokens. */
    public string Owner { get; }

    /** SHA-256 of the token: what a per-token cache is keyed by, never the token itself. Empty for the server. */
    public string TokenHash { get; }

    public bool IsUser => AccessToken != null;

    /** A person who came in with their own token; issuer and subject come from the checked token. */
    internal static Caller Person(string accessToken, string name, string issuer, string subject) =>
        new(accessToken, name, "user:" + Sha256(issuer + "\n" + subject), Sha256(accessToken));

    public override string ToString() => IsUser ? "the person " + Name : Name;

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // --------------------------------------------------------------------- the current request's caller

    private sealed class Holder
    {
        public Caller? Caller;
    }

    private static readonly AsyncLocal<Holder?> Ambient = new();

    /** The caller of the request this code runs for; null outside a request (stdio, background services). */
    public static Caller? Current => Ambient.Value?.Caller;

    /** Makes caller current for what follows in this async flow, until the returned scope is disposed. */
    public static IDisposable Enter(Caller caller)
    {
        var holder = new Holder { Caller = caller };
        Ambient.Value = holder;
        return new Scope(holder);
    }

    private sealed class Scope(Holder holder) : IDisposable
    {
        // Emptied, not swapped: a context captured during the request still points at this holder.
        public void Dispose() => holder.Caller = null;
    }
}
