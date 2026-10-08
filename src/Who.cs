using System.Collections.Concurrent;

namespace DmcMcp;

/// <summary>
/// Who a person's token is in DMC — GET /auth/me with that token: the name and the roles. The disk gate asks before
/// the server keeps a file for a person (<see cref="DiskGate"/>), and the errors that name the account ask too
/// ("…as anna@example.com, who is not a Publisher in DMC"). Remembered for a minute per token, keyed by the token's
/// hash, never the token; null when DMC does not take the token or does not answer within 20 seconds.
/// </summary>
public sealed class Who(IDmcClient dmc, TimeProvider? clock = null)
{
    internal static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);
    /** /auth/me answers in well under a second; a DMC that hangs must not hold an upload for the client's five minutes. */
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private const int MaxRemembered = 512;
    private readonly ConcurrentDictionary<string, (MeInfo? Me, DateTimeOffset Until)> _seen = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<MeInfo?> Of(Caller person)
    {
        var now = _clock.GetUtcNow();
        if (_seen.TryGetValue(person.TokenHash, out var hit) && hit.Until > now) return hit.Me;
        MeInfo? me;
        try { me = await dmc.Me(person.AccessToken!).WaitAsync(Patience); }
        catch (Exception) { me = null; } // the message then says what it can without it
        if (_seen.Count >= MaxRemembered)
            foreach (var old in _seen.Where(kv => kv.Value.Until <= now).ToList()) _seen.TryRemove(old.Key, out _);
        if (_seen.Count < MaxRemembered) _seen[person.TokenHash] = (me, now + Keep);
        return me;
    }
}
