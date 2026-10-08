namespace DmcMcp;

/// <summary>
/// The hosted server's own disk and CPU — an upload received, a link downloaded, a zip unpacked — are not something
/// the DMC API offers, so a token DMC takes does not buy them: any licsys account can mint one. The key's account is
/// the operator's own and is bound by the totals alone. A person who came in with their own token must first be
/// someone DMC lets publish: before anything of theirs is stored, downloaded or unpacked, DMC is asked who they are
/// (GET /auth/me with their token, through <see cref="Who"/> — the call DMC's own site makes at sign-in, remembered a
/// minute per token), and only a Publisher, or an Admin, goes on. Then a share: at most <see cref="MaxJobs"/>
/// downloads or unpackings run at once, and one of them a person's. Uploads have their own slots and shares
/// (<see cref="UploadStore"/>).
/// </summary>
public sealed class DiskGate(Who who)
{
    public const int MaxJobs = 2;
    public const int MaxJobsPerPerson = 1;

    private readonly Slots _jobs = new(MaxJobs, MaxJobsPerPerson);

    /** Why the server keeps nothing for a caller: 403 — DMC does not let them publish; 503 — DMC did not say who they are. */
    public sealed record Refusal(int Status, string Message);

    /** Null when the caller may have files kept on this server; else why not. The key's account is not asked about. */
    public async Task<Refusal?> Check(Caller caller)
    {
        if (!caller.IsUser) return null;
        var me = await who.Of(caller);
        if (me == null)
            return new Refusal(503, $"DMC did not confirm who {caller.Name} is — it did not answer, or did not take the token — "
                                    + "so this server keeps no file for them yet; try again in a minute, with a fresh token if this one is old");
        if (!me.IsPublisher)
            return new Refusal(403, $"{(me.Username is { Length: > 0 } name ? name : caller.Name)} is not a Publisher in DMC, "
                                    + "so this server keeps no file for them — a DMC administrator grants the role (Account → Users)");
        return null;
    }

    /** A slot for one download or unpacking — dispose it when the work is done — or null, and why. */
    public IDisposable? TryBegin(Caller caller, out string? busy)
    {
        var slot = _jobs.TryTake(caller.Owner, out var full);
        busy = full switch
        {
            Slots.Full.Mine => "you already have a download or an unpacking running on this server — call again when it has finished",
            Slots.Full.Everyone => $"the server is already downloading or unpacking {MaxJobs} files — call again in a few minutes",
            _ => null,
        };
        return slot;
    }
}
