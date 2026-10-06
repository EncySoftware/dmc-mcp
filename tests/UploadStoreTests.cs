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

    [Fact]
    public async Task ReferenceIsCaseInsensitive()
    {
        var saved = await Store().Save(new MemoryStream(new byte[] { 1 }), "a.zip");
        Assert.NotNull(Store().Resolve("UPLOAD:" + saved.Id.ToUpperInvariant()));
    }
}
