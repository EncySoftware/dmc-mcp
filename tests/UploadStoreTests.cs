using DmcMcp;
using Xunit;

/** upload:<id> — how a hosted client hands the server a file it cannot read from the client's disk. */
public class UploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmc-up-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private UploadStore Store() => new(_root, () => _now);

    [Fact]
    public async Task SavedFileResolvesByItsReference()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 7, 8 }), "Fanuc 0i.sppx");
        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
        Assert.Equal("upload:" + saved.Id, saved.Ref);
        Assert.Equal(2, saved.Size);
        Assert.Equal(_now + TimeSpan.FromHours(24), saved.ExpiresAt);
        var path = Store().Resolve(saved.Ref);
        Assert.Equal("Fanuc 0i.sppx", Path.GetFileName(path));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(path!));
    }

    [Fact]
    public async Task ExpiredUploadIsGoneAndPurged()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        _now += TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);
        Assert.Null(Store().Resolve(saved.Ref));
        Assert.Equal(1, Store().Purge());
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("upload:../../etc")]
    [InlineData("upload:")]
    [InlineData("upload:0123")]
    [InlineData("upload:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("/etc/passwd")]
    [InlineData("upload:0123456789abcdef0123456789abcdef")]
    public void UnknownOrMalformedReferencesResolveToNothing(string reference) =>
        Assert.Null(Store().Resolve(reference));

    [Fact]
    public async Task NameCannotClimbOutOfItsFolder()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "..\\..\\evil.sppx");
        Assert.Equal("evil.sppx", saved.Name);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(Store().Resolve(saved.Ref)!));
    }

    /** 1 GB a file and 24 hours each, but not without end: past the total the store refuses, keeping nothing. */
    [Fact]
    public async Task AFullStoreRefusesAndKeepsNothingOfTheRefusedFile()
    {
        var store = new UploadStore(_root, () => _now) { MaxTotalBytes = 10 };
        await store.Save(new MemoryStream(new byte[8]), "a.sppx");
        await Assert.ThrowsAsync<UploadStore.FullException>(() => store.Save(new MemoryStream(new byte[8]), "b.sppx"));
        Assert.Single(Directory.GetDirectories(_root));
        Assert.Equal(8, store.UsedBytes());
    }

    /** ReadFormAsync buffers each upload on the container's disk before Save: at most two at a time. */
    [Fact]
    public void AtMostTwoUploadsAtOnce()
    {
        var store = Store();
        using var a = store.TryBegin();
        var b = store.TryBegin();
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(store.TryBegin());
        b!.Dispose();
        using var c = store.TryBegin();
        Assert.NotNull(c);
    }

    // ------------------------------------------------------------- owners (0.9.0)

    [Fact]
    public async Task AnotherOwnersUploadIsNotFound()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.sppx", "user:aaa");
        Assert.NotNull(Store().Resolve(saved.Ref, "user:aaa"));
        Assert.Null(Store().Resolve(saved.Ref, "user:bbb"));
        Assert.Null(Store().Resolve(saved.Ref, Caller.Server.Owner));
    }

    [Fact]
    public async Task WithoutAnOwnerAnUploadIsTheKeys()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.sppx");
        Assert.NotNull(Store().Resolve(saved.Ref));
        Assert.NotNull(Store().Resolve(saved.Ref, Caller.Server.Owner));
        Assert.Null(Store().Resolve(saved.Ref, "user:aaa"));
    }

    /** Uploads made by 0.8.0 carry no owner: they were all made with the key, so they stay the key's. */
    [Fact]
    public void AnUploadFromBeforeOwnersIsTheKeys()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.sppx"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(dir, ".expires"), (_now + TimeSpan.FromHours(1)).ToUnixTimeSeconds().ToString());
        Assert.NotNull(Store().Resolve("upload:" + id, Caller.Server.Owner));
        Assert.Null(Store().Resolve("upload:" + id, "user:aaa"));
    }

    /** A file named like the store's own bookkeeping cannot overwrite it — nor be mistaken for it. */
    [Theory]
    [InlineData(".owner")]
    [InlineData(".expires")]
    [InlineData(".OWNER")]
    public async Task AFileNamedLikeTheStoresOwnKeepsItsOwner(string name)
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 5, 6 }), name, "user:aaa");
        var path = Store().Resolve(saved.Ref, "user:aaa");
        Assert.NotNull(path);
        Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(path!));
        Assert.Null(Store().Resolve(saved.Ref, Caller.Server.Owner));
        Assert.Null(Store().Resolve(saved.Ref, "user:bbb"));
    }

    /** The owner note is bookkeeping, not an upload: it does not count against the 5 GB. */
    [Fact]
    public async Task TheOwnerNoteIsNotCounted()
    {
        var store = Store();
        await store.Save(new MemoryStream(new byte[] { 1, 2 }), "a.sppx", "user:aaa");
        Assert.Equal(2, store.UsedBytes());
    }

    // ------------------------------------------------------------- shares (0.9.0)

    private const string Anna = "user:anna";
    private const string Boris = "user:boris";

    /** Two uploads at a time in all, one of them a person's: one person cannot take both slots while the rest wait. */
    [Fact]
    public void APersonRunsOneUploadAtATime()
    {
        var store = Store();
        var first = store.TryBegin(Anna, out var none);
        Assert.NotNull(first);
        Assert.Null(none);
        Assert.Null(store.TryBegin(Anna, out var mine));
        Assert.Contains("you already have an upload running", mine);
        using var boris = store.TryBegin(Boris, out _);
        Assert.NotNull(boris);
        Assert.Null(store.TryBegin("user:carol", out var everyone));
        Assert.Contains("2 uploads are already running", everyone);
        first!.Dispose();
        using var again = store.TryBegin(Anna, out _);
        Assert.NotNull(again);
    }

    /** The key is the operator's own: as in 0.8.0 it may run both uploads itself. */
    [Fact]
    public void TheKeyMayRunBothUploads()
    {
        var store = Store();
        using var a = store.TryBegin(Caller.Server.Owner, out _);
        using var b = store.TryBegin(Caller.Server.Owner, out _);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    /** A person's uploads count against a share of their own as well as the total: nobody fills the store for everyone. */
    [Fact]
    public async Task APersonHasAShareOfTheStore()
    {
        var store = new UploadStore(_root, () => _now) { MaxTotalBytes = 100, MaxBytesPerPerson = 10 };
        await store.Save(new MemoryStream(new byte[8]), "a.sppx", Anna);
        Assert.Equal(8, store.UsedBytes(Anna));
        var over = await Assert.ThrowsAsync<UploadStore.FullException>(() => store.Save(new MemoryStream(new byte[8]), "b.sppx", Anna));
        Assert.Contains("your share", over.Message);
        Assert.Single(Directory.GetDirectories(_root)); // nothing of the refused file stays
        await store.Save(new MemoryStream(new byte[8]), "c.sppx", Boris);
        await store.Save(new MemoryStream(new byte[20]), "d.sppx"); // the key: the total alone
        Assert.Equal(0, store.UsedBytes("user:carol"));
        Assert.Equal(36, store.UsedBytes());
    }

    /** Before the body is read: is there room for this sender at all — in the store, and in their share. */
    [Fact]
    public async Task NoRoomIsSaidBeforeTheBodyIsRead()
    {
        var store = new UploadStore(_root, () => _now) { MaxTotalBytes = 100, MaxBytesPerPerson = 10 };
        Assert.Null(store.NoRoomFor(Anna));
        await store.Save(new MemoryStream(new byte[10]), "a.sppx", Anna);
        Assert.Contains("your share", store.NoRoomFor(Anna));
        Assert.Null(store.NoRoomFor(Boris));
        Assert.Null(store.NoRoomFor(Caller.Server.Owner));
        _now += UploadStore.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(store.NoRoomFor(Anna)); // an expired upload gives its room back
    }

    [Fact]
    public async Task ReferenceIsCaseInsensitive()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.zip");
        Assert.NotNull(Store().Resolve("UPLOAD:" + saved.Id.ToUpperInvariant()));
    }
}
