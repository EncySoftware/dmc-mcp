using System.Text.Json;

namespace DmcMcp;

/// <summary>
/// A hint about a newer version: the version list from nuget.org against our own. Any failure means silence:
/// this is a courtesy note, not a check, and it must get in the way of neither the server nor <c>setup</c>.
/// </summary>
public static class VersionCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/encysoftware.dmcmcp/index.json";

    /** This build's version, as "0.3.1". */
    public static string Current =>
        typeof(VersionCheck).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "0.0.0";

    /** The newest version from the nuget.org JSON if it is newer than current; otherwise null. */
    public static string? NewerThan(string current, string versionsJson)
    {
        if (!TryParse(current, out var mine)) return null;
        try
        {
            using var doc = JsonDocument.Parse(versionsJson);
            if (!doc.RootElement.TryGetProperty("versions", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            Version? best = null;
            string? bestText = null;
            foreach (var v in arr.EnumerateArray())
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (s == null || !TryParse(s, out var parsed)) continue;
                if (parsed > mine && (best == null || parsed > best)) { best = parsed; bestText = s; }
            }
            return bestText;
        }
        catch (JsonException) { return null; }
    }

    /** A line for a human, or null — with no network, on an error, and when the version is current. */
    public static async Task<string?> Hint()
    {
        try
        {
            var newer = NewerThan(Current, await Http.GetStringAsync(IndexUrl));
            return newer == null ? null
                : $"Version {newer} is available (you have {Current}): dotnet tool update -g EncySoftware.DmcMcp";
        }
        catch (Exception) { return null; }
    }

    /** The assembly's "0.3.1.0" and the package's "0.3.1" are the same; a "-beta" tail is dropped. */
    private static bool TryParse(string s, out Version v)
    {
        var core = (s ?? "").Split('-', '+')[0];
        if (Version.TryParse(core, out var parsed) && parsed != null)
        {
            v = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), 0);
            return true;
        }
        v = new Version(0, 0);
        return false;
    }
}
