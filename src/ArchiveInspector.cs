using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DmcMcp;

/// <summary>
/// Facts about a component archive — locally, with no server and no AI: what is inside, what the machine is
/// called, which axes and travels, what equipment, whether there is a picture, which controls are mentioned.
/// From them the agent writes the description itself and fills in the fields when the author wants no server AI.
/// </summary>
public static class ArchiveInspector
{
    /** Controls and makers worth noticing in the text: they tell the agent who the post is for. */
    internal static readonly string[] Keywords =
    {
        "Fanuc", "Siemens", "Sinumerik", "Heidenhain", "Haas", "Mazak", "Mazatrol", "Okuma", "OSP", "Mitsubishi",
        "Fagor", "Hurco", "Brother", "Doosan", "DMG", "Tormach", "PathPilot", "LinuxCNC", "Mach3", "GRBL", "Fidia",
        "Selca", "Hypertherm", "Beckhoff", "Bosch", "Yaskawa", "Kuka", "ABB", "Hermle", "Makino", "Citizen", "Star",
    };

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    private static readonly string[] PostExt = { ".sppx", ".dll" };
    private static readonly string[] TextExt = { ".xml", ".txt", ".json", ".ini", ".cfg", ".sppx", ".stnci", ".md", ".csv" };

    public static string Inspect(string file)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return $"ERROR: file {path} does not exist.";
        var info = new FileInfo(path);
        var sb = new StringBuilder();
        sb.AppendLine($"File: {info.Name} ({Size(info.Length)})");
        var keywords = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!IsZip(path))
        {
            // A .sppx or .dll without a zip header — read it as text: control names are usually in it.
            sb.AppendLine("Format: not a zip — reading it as text");
            FindKeywords(ReadHead(path), keywords);
            sb.AppendLine(keywords.Count > 0 ? "Mentioned: " + string.Join(", ", keywords) : "No known controls or machines in the text");
            return sb.ToString();
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith("/")).ToList();
            sb.AppendLine($"Zip: {entries.Count} file(s)");
            var images = entries.Where(e => ImageExt.Contains(Ext(e))).Select(e => e.FullName).ToList();
            var posts = entries.Where(e => PostExt.Contains(Ext(e))).Select(e => e.FullName).ToList();
            var interp = entries.Where(e => Ext(e) == ".stnci").Select(e => e.FullName).ToList();
            var xmls = entries.Where(e => Ext(e) == ".xml").ToList();
            bool schema = xmls.Count > 0 && entries.Any(e => Ext(e) == ".osd");
            var kind = schema && (posts.Count > 0 || interp.Count > 0) ? "kit (schema + " + (posts.Count > 0 ? "posts" : "interpreter") + ")"
                     : schema ? "machine schema (xml + osd)"
                     : posts.Count > 0 ? "post(s)"
                     : interp.Count > 0 ? "interpreter"
                     : "not recognised";
            sb.AppendLine("Looks like: " + kind);

            foreach (var x in xmls)
            {
                var text = ReadEntry(x);
                var parsed = ParseSchema(text);
                if (parsed != null) sb.Append(parsed);
                FindKeywords(text, keywords);
            }
            foreach (var e in entries.Where(e => Ext(e) != ".xml" && TextExt.Contains(Ext(e)) && e.Length < 4 * 1024 * 1024))
                FindKeywords(ReadEntry(e), keywords);

            if (posts.Count > 0) sb.AppendLine("Posts: " + string.Join(", ", posts));
            if (interp.Count > 0) sb.AppendLine("Interpreters: " + string.Join(", ", interp));
            sb.AppendLine("Picture inside: " + (images.Count > 0 ? string.Join(", ", images) : "none"));
            if (keywords.Count > 0) sb.AppendLine("Mentioned: " + string.Join(", ", keywords));
        }
        catch (InvalidDataException) { sb.AppendLine("The zip is damaged — it could not be read."); }
        return sb.ToString();
    }

    /**
     * Machine name, axes with travels and equipment from an ENCY schema XML — as much as a description needs.
     * The format the backend reads: a `TRegisterMachineRecord` record with Caption, axes in `MachineStateParameters`
     * with child `Group`/`Address`/`Min`/`Max`, equipment as `TCaseNode` selectors. A parse failure gives null.
     */
    internal static string? ParseSchema(string xml)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml.TrimStart('﻿'));
            var sb = new StringBuilder();

            string? machine = null;
            foreach (XmlElement el in doc.GetElementsByTagName("SCType"))
                if (TypeOf(el) == "TRegisterMachineRecord") { machine = el.GetAttribute("Caption"); break; }
            if (!string.IsNullOrWhiteSpace(machine)) sb.AppendLine("Machine (from the schema): " + machine);

            var linear = new List<(string Addr, string? Range)>();
            var rotary = new List<string>();
            foreach (XmlElement msp in doc.GetElementsByTagName("MachineStateParameters"))
                foreach (XmlNode n in msp.ChildNodes)
                {
                    if (n is not XmlElement sc || sc.Name != "SCType") continue;
                    var addr = ChildDefault(sc, "Address");
                    if (addr == null) continue;
                    var group = ChildDefault(sc, "Group");
                    var min = ChildDefault(sc, "Min");
                    var max = ChildDefault(sc, "Max");
                    if (group == "LinearAxis") linear.Add((addr, min != null && max != null ? $"{addr} {min}…{max}" : null));
                    else if (group == "RotaryAxis") rotary.Add(addr);
                }
            if (linear.Count > 0)
            {
                sb.Append("Linear axes: " + string.Join(", ", linear.Select(a => a.Addr)));
                var ranges = linear.Where(a => a.Range != null).Select(a => a.Range).ToList();
                sb.AppendLine(ranges.Count > 0 ? "; travels: " + string.Join(", ", ranges) : "");
            }
            if (rotary.Count > 0) sb.AppendLine("Rotary axes: " + string.Join(", ", rotary));

            var equipment = new List<string>();
            foreach (XmlElement el in doc.GetElementsByTagName("SCType"))
            {
                if (TypeOf(el) != "TCaseNode") continue;
                var cap = el.GetAttribute("Caption");
                if (cap.Length == 0) cap = el.GetAttribute("ID");
                cap = Regex.Replace(cap.Replace('_', ' '), @"(?i)\s*selector\s*$", "").Trim();
                if (cap.Length > 0 && !equipment.Contains(cap)) equipment.Add(cap);
            }
            if (equipment.Count > 0) sb.AppendLine("Equipment (selectors): " + string.Join(", ", equipment));
            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (XmlException) { return null; }
    }

    /** In ENCY files the type attribute appears both as `type` and as `Type`. */
    private static string TypeOf(XmlElement el)
    {
        var t = el.GetAttribute("type");
        return t.Length > 0 ? t : el.GetAttribute("Type");
    }

    private static string? ChildDefault(XmlElement sc, string tag)
    {
        foreach (XmlNode n in sc.ChildNodes)
            if (n is XmlElement el && el.Name == tag)
            {
                var v = el.GetAttribute("DefaultValue");
                return v.Length == 0 ? null : v;
            }
        return null;
    }

    private static void FindKeywords(string text, SortedSet<string> into)
    {
        foreach (var k in Keywords)
            if (Regex.IsMatch(text, @"\b" + Regex.Escape(k) + @"\b", RegexOptions.IgnoreCase)) into.Add(k);
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            return s.ReadByte() == 'P' && s.ReadByte() == 'K';
        }
        catch (IOException) { return false; }
    }

    private static string Ext(ZipArchiveEntry e) => Path.GetExtension(e.Name).ToLowerInvariant();

    private static string ReadEntry(ZipArchiveEntry e)
    {
        using var r = new StreamReader(e.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    /** The first 2 MB as text — for a non-zip .sppx/.dll; binary garbage does not hurt the keyword search. */
    private static string ReadHead(string path)
    {
        using var s = File.OpenRead(path);
        var buf = new byte[Math.Min(s.Length, 2L * 1024 * 1024)];
        int read = s.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";
}
