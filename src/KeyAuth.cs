using System.Security.Cryptography;
using System.Text;

namespace DmcMcp;

/// <summary>
/// The hosted server's door: requests under /mcp carry the key as `Authorization: Bearer &lt;key&gt;`, or — for
/// clients configured by URL alone — as the first path segment, /mcp/&lt;key&gt;[/…], which is stripped before
/// routing. Comparisons are constant-time. Nothing outside /mcp is the server's.
/// </summary>
public static class KeyAuth
{
    public static (bool Ok, string Path) Check(string path, string? authorization, string key)
    {
        if (path != "/mcp" && !path.StartsWith("/mcp/", StringComparison.Ordinal)) return (false, path);
        // The path first: a client configured with the key in its URL may send the header too, and the segment
        // must go either way, or nothing routes and the client sees a 404 that looks like a wrong address.
        if (path.StartsWith("/mcp/", StringComparison.Ordinal))
        {
            var rest = path["/mcp/".Length..];
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (Same(segment, key)) return (true, "/mcp" + (slash < 0 ? "" : rest[slash..]));
        }
        if (authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && Same(authorization["Bearer ".Length..].Trim(), key))
            return (true, path);
        return (false, path);
    }

    private static bool Same(string given, string key) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
}
