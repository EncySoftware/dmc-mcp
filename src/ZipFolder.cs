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

    public static (string? Dir, string? Error) Extract(string zipPath, string intoDir)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries)
                return (null, $"ERROR: the zip has {zip.Entries.Count} entries; at most {MaxEntries}.");
            long total = 0;
            foreach (var e in zip.Entries) total += e.Length;
            if (total > MaxBytes) return (null, "ERROR: the zip unpacks to more than 1 GB.");

            var root = Path.GetFullPath(intoDir);
            foreach (var e in zip.Entries)
            {
                var rel = e.FullName.Replace('\\', '/');
                if (rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Contains(".."))
                    return (null, $"ERROR: the zip entry \"{e.FullName}\" points outside the folder.");
            }
            Directory.CreateDirectory(root);
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
                e.ExtractToFile(dest, overwrite: true);
            }

            var top = Directory.GetFileSystemEntries(root);
            if (top.Length == 1 && Directory.Exists(top[0]) && Directory.GetFiles(top[0], "*.xml").Length == 0)
                return (top[0], null);
            return (root, null);
        }
        catch (InvalidDataException) { return (null, "ERROR: the file is not a readable zip."); }
    }
}
