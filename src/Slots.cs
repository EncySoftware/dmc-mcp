namespace DmcMcp;

/// <summary>
/// Slots for work that costs the hosted server's own disk — an upload being received, a link being downloaded, a zip
/// being unpacked: at most <c>total</c> at once, and at most <c>perPerson</c> of them for any one person who came in
/// with their own token, so that nobody holds them all while the rest wait. The key's account — the operator's own,
/// one account behind every call made with the key — is bound by the total alone, as it was in 0.8.0.
/// </summary>
public sealed class Slots(int total, int perPerson)
{
    /** Whose limit a refused slot hit: the person's own, or everyone's together. */
    public enum Full { No, Mine, Everyone }

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);
    private int _all;

    /** A slot for owner (<see cref="Caller.Owner"/>) — dispose it when the work is done — or null, and whose limit. */
    public IDisposable? TryTake(string owner, out Full full)
    {
        var person = owner != Caller.Server.Owner;
        lock (_lock)
        {
            var mine = _held.GetValueOrDefault(owner);
            full = person && mine >= perPerson ? Full.Mine : _all >= total ? Full.Everyone : Full.No;
            if (full != Full.No) return null;
            _all++;
            _held[owner] = mine + 1;
        }
        return new Slot(this, owner);
    }

    private void Give(string owner)
    {
        lock (_lock)
        {
            _all--;
            if (--_held[owner] == 0) _held.Remove(owner);
        }
    }

    private sealed class Slot(Slots slots, string owner) : IDisposable
    {
        private int _given;
        public void Dispose() { if (Interlocked.Exchange(ref _given, 1) == 0) slots.Give(owner); }
    }
}
