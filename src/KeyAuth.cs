using System.Security.Cryptography;
using System.Text;

namespace DmcMcp;

/// <summary>
/// The hosted server's key: requests under /mcp carry it as `Authorization: Bearer &lt;key&gt;`, or — for clients
/// configured by URL alone — as the first path segment, /mcp/&lt;key&gt;[/…], which is stripped before routing.
/// Comparisons are constant-time. Nothing outside /mcp is the server's. <see cref="Door"/> builds on these pieces
/// to let a person's own token in as well.
/// </summary>
public static class KeyAuth
{
    public static (bool Ok, string Path) Check(string path, string? authorization, string key)
    {
        if (!UnderMcp(path)) return (false, path);
        // The path first: a client configured with the key in its URL may send the header too, and the segment
        // must go either way, or nothing routes and the client sees a 404 that looks like a wrong address.
        var (inPath, rest) = InPath(path, key);
        if (inPath) return (true, rest);
        return (Bearer(authorization) is { } given && IsKey(given, key), path);
    }

    public static bool UnderMcp(string path) => path == "/mcp" || path.StartsWith("/mcp/", StringComparison.Ordinal);

    /** The key as the first segment after /mcp/: whether it is there, and the path without it. */
    public static (bool Found, string Path) InPath(string path, string key)
    {
        if (!path.StartsWith("/mcp/", StringComparison.Ordinal)) return (false, path);
        var rest = path["/mcp/".Length..];
        var slash = rest.IndexOf('/');
        var segment = slash < 0 ? rest : rest[..slash];
        return IsKey(segment, key) ? (true, "/mcp" + (slash < 0 ? "" : rest[slash..])) : (false, path);
    }

    /** The credential of an `Authorization: Bearer …` header; null for none or another scheme. */
    public static string? Bearer(string? authorization) =>
        authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;

    public static bool IsKey(string given, string key) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
}
