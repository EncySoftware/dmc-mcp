using System.Text.Json;

namespace DmcMcp;

/// <summary>
/// Подсказка о новой версии: список версий с nuget.org против своей. Любой сбой — молчание: это
/// справка, а не проверка, и она не должна мешать ни серверу, ни <c>setup</c>.
/// </summary>
public static class VersionCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/encysoftware.dmcmcp/index.json";

    /** Версия этой сборки в виде «0.3.1». */
    public static string Current =>
        typeof(VersionCheck).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "0.0.0";

    /** Новейшая версия из JSON nuget.org, если она новее current; иначе null. */
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

    /** Строка для человека или null — без сети, при ошибке и когда версия актуальна. */
    public static async Task<string?> Hint()
    {
        try
        {
            var newer = NewerThan(Current, await Http.GetStringAsync(IndexUrl));
            return newer == null ? null
                : $"Доступна версия {newer} (у вас {Current}): dotnet tool update -g EncySoftware.DmcMcp";
        }
        catch (Exception) { return null; }
    }

    /** «0.3.1.0» сборки и «0.3.1» пакета — одно и то же; хвост «-beta» отбрасывается. */
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
