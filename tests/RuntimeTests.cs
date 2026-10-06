using System.Text.Json;
using Xunit;

/**
 * .NET 8 leaves support on 10 November 2026, and the hosted server faces the internet. The tool stays built for
 * net8.0 — a publisher with only the .NET 8 SDK keeps working — but may run on a newer runtime, which the image
 * (aspnet:10.0) has alone; by default a runtimeconfig allows only newer minors of 8.
 */
public class RuntimeTests
{
    [Fact]
    public void TheToolRollsForwardToANewerMajorRuntime()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dmc-mcp.runtimeconfig.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var options = doc.RootElement.GetProperty("runtimeOptions");
        Assert.True(options.TryGetProperty("rollForward", out var roll), "no rollForward in " + path);
        Assert.Equal("Major", roll.GetString());
    }

    [Fact]
    public void TheImageRunsOnASupportedRuntime()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0", dockerfile);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dockerfile"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Dockerfile not found above " + AppContext.BaseDirectory);
    }
}
