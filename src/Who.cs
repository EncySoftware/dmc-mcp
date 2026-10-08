using System.Collections.Concurrent;

namespace DmcMcp;

/// <summary>
/// Who a person's token is in DMC — GET /auth/me with that token: the name and the roles an error message needs
/// ("…as anna@example.com, who is not a Publisher in DMC"). Remembered for a minute per token, keyed by the token's
/// hash, never the token; null when DMC does not take the token or does not answer.
/// </summary>
internal sealed class Who(IDmcClient dmc, TimeProvider? clock = null)
{
    internal static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);
    private const int MaxRemembered = 512;
    private readonly ConcurrentDictionary<string, (MeInfo? Me, DateTimeOffset Until)> _seen = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<MeInfo?> Of(Caller person)
    {
        var now = _clock.GetUtcNow();
        if (_seen.TryGetValue(person.TokenHash, out var hit) && hit.Until > now) return hit.Me;
        MeInfo? me;
        try { me = await dmc.Me(person.AccessToken!); }
        catch (Exception) { me = null; } // the message then says what it can without it
        if (_seen.Count >= MaxRemembered)
            foreach (var old in _seen.Where(kv => kv.Value.Until <= now).ToList()) _seen.TryRemove(old.Key, out _);
        if (_seen.Count < MaxRemembered) _seen[person.TokenHash] = (me, now + Keep);
        return me;
    }
}
