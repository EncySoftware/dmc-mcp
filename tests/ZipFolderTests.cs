using System.IO.Compression;
using DmcMcp;
using Xunit;

/** On the hosted server publish_folder gets the folder as a zip; unpacking it must stay inside a temp folder. */
public class ZipFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-zf-" + Guid.NewGuid().ToString("N")[..8]);
    public ZipFolderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Zip(params string[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6] + ".zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries)
        {
            var entry = z.CreateEntry(e);
            if (!e.EndsWith('/')) using (var w = new StreamWriter(entry.Open())) w.Write("x");
        }
        return path;
    }

    [Fact]
    public void ContentsZipUnpacksAsTheFolder()
    {
        var (dir, error) = ZipFolder.Extract(Zip("a.sppx", "Haas VF-2/Haas VF-2.xml", "Haas VF-2/Images/base.osd"), Path.Combine(_dir, "out"));
        Assert.Null(error);
        Assert.True(File.Exists(Path.Combine(dir!, "a.sppx")));
        Assert.True(File.Exists(Path.Combine(dir!, "Haas VF-2", "Haas VF-2.xml")));
    }

    /** People zip the folder itself; a single top folder that is not a schema is the folder to publish. */
    [Fact]
    public void ZipOfTheFolderItselfUnpacksIntoIt()
    {
        var (dir, _) = ZipFolder.Extract(Zip("posts/a.sppx", "posts/b.sppx"), Path.Combine(_dir, "out"));
        Assert.Equal("posts", Path.GetFileName(dir));
    }

    /** But a single schema folder (xml directly inside) is one component, not the folder to publish. */
    [Fact]
    public void SingleSchemaFolderStaysAComponent()
    {
        var (dir, _) = ZipFolder.Extract(Zip("Haas VF-2/Haas VF-2.xml", "Haas VF-2/Images/base.osd"), Path.Combine(_dir, "out"));
        Assert.Equal("out", Path.GetFileName(dir));
    }

    [Theory]
    [InlineData("../evil.sppx")]
    [InlineData("a/../../evil.sppx")]
    [InlineData("/etc/evil.sppx")]
    [InlineData("C:/evil.sppx")]
    public void EntriesPointingOutsideAreRefused(string entry)
    {
        var (dir, error) = ZipFolder.Extract(Zip(entry), Path.Combine(_dir, "out"));
        Assert.Null(dir);
        Assert.Contains("points outside", error);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.sppx")));
    }

    [Fact]
    public void NotAZipIsReported()
    {
        var path = Path.Combine(_dir, "x.zip");
        File.WriteAllText(path, "not a zip");
        var (dir, error) = ZipFolder.Extract(path, Path.Combine(_dir, "out"));
        Assert.Null(dir);
        Assert.Contains("not a readable zip", error);
    }
}
