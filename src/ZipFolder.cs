using System.IO.Compression;

namespace DmcMcp;

/// <summary>
/// publish_folder's folder, sent to the hosted server as a zip: unpacked into a fresh folder with no entry allowed
/// out of it (absolute paths, drive letters, ".." segments), at most 5000 entries and 1 GB unpacked. A zip of the
/// folder itself — one top folder and nothing beside it — publishes that folder, unless that folder is a schema
/// (an .xml directly inside), which is one component.
/// </summary>
public static class ZipFolder
{
    public const int MaxEntries = 5000;
    public const long MaxBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Unpacks into intoDir, a fresh folder. The size limit counts the bytes actually written, not the sizes the
    /// zip declares: a Stored entry is read for its compressed length whatever its Length says, and central
    /// directory records may point at the same data. On an error intoDir is removed.
    /// </summary>
    public static (string? Dir, string? Error) Extract(string zipPath, string intoDir, long maxBytes = MaxBytes)
    {
        var tooBig = "ERROR: the zip unpacks to more than " + (maxBytes >= 1L << 30 ? $"{maxBytes >> 30} GB." : $"{maxBytes} bytes.");
        var root = Path.GetFullPath(intoDir);
        var (dir, error) = Unpack(zipPath, root, maxBytes, tooBig);
        if (error != null) try { Directory.Delete(root, true); } catch { /* nothing was written, or the caller sweeps */ }
        return (dir, error);
    }

    private static (string? Dir, string? Error) Unpack(string zipPath, string root, long maxBytes, string tooBig)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries)
                return (null, $"ERROR: the zip has {zip.Entries.Count} entries; at most {MaxEntries}.");
            long declared = 0;
            foreach (var e in zip.Entries) declared += e.Length;
            if (declared > maxBytes) return (null, tooBig); // an honest zip is refused before anything is written

            foreach (var e in zip.Entries)
            {
                var rel = e.FullName.Replace('\\', '/');
                if (rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Contains(".."))
                    return (null, $"ERROR: the zip entry \"{e.FullName}\" points outside the folder.");
            }
            Directory.CreateDirectory(root);
            long written = 0;
            var buffer = new byte[81920];
            foreach (var e in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('\\', '/')));
                if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return (null, $"ERROR: the zip entry \"{e.FullName}\" points outside the folder.");
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = e.Open();
                using var dst = File.Create(dest);
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > maxBytes) return (null, tooBig);
                    dst.Write(buffer, 0, n);
                }
            }

            var top = Directory.GetFileSystemEntries(root);
            if (top.Length == 1 && Directory.Exists(top[0]) && Directory.GetFiles(top[0], "*.xml").Length == 0)
                return (top[0], null);
            return (root, null);
        }
        catch (InvalidDataException) { return (null, "ERROR: the file is not a readable zip."); }
    }
}
