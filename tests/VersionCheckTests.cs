using DmcMcp;
using Xunit;

/** The newer-version hint: read nuget.org's version list and compare it with our own; any failure means silence. */
public class VersionCheckTests
{
    [Fact]
    public void NamesTheNewestWhenItIsNewer()
    {
        Assert.Equal("0.4.0", VersionCheck.NewerThan("0.3.1", """{"versions":["0.1.0","0.2.0","0.3.1","0.4.0"]}"""));
    }

    [Fact]
    public void SilentWhenUpToDate()
    {
        Assert.Null(VersionCheck.NewerThan("0.4.0", """{"versions":["0.1.0","0.4.0"]}"""));
        Assert.Null(VersionCheck.NewerThan("0.4.0", """{"versions":["0.3.9"]}"""));
    }

    [Fact]
    public void SilentOnGarbage()
    {
        Assert.Null(VersionCheck.NewerThan("0.4.0", "not json"));
        Assert.Null(VersionCheck.NewerThan("0.4.0", """{"versions":["weird"]}"""));
        Assert.Null(VersionCheck.NewerThan("", """{"versions":["1.0.0"]}"""));
    }

    /** The assembly version is "0.3.1.0"; the fourth zero must not get in the way of the comparison. */
    [Fact]
    public void AcceptsAFourPartAssemblyVersion()
    {
        Assert.Equal("0.4.0", VersionCheck.NewerThan("0.3.1.0", """{"versions":["0.4.0"]}"""));
        Assert.Null(VersionCheck.NewerThan("0.4.0.0", """{"versions":["0.4.0"]}"""));
    }
}
