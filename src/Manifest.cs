using System.Text;

namespace DmcMcp;

/** A manifest row: the name (if given) and the card fields for one file. */
internal sealed record ManifestEntry(string? Name, DmcTools.FieldSet Fields);

/**
 * The author's CSV next to the posts — exact data instead of AI guesses:
 * <c>file,name,controllerManufacturer,…</c>. Only the <c>file</c> column is required.
 * An unknown column is an error, not silence: otherwise a typo in the header would quietly drop
 * a whole column, and the author would find out from the cards.
 */
internal sealed class Manifest
{
    internal static readonly string[] Columns =
    {
        "file", "name", "description", "controllerManufacturer", "controllerSeries", "controllerModel",
        "machineManufacturer", "machineSeries", "machineModel", "machineType", "numberOfAxes",
        "travelXMm", "travelYMm", "travelZMm",
    };

    internal const long MaxBytes = 10L * 1024 * 1024;

    private readonly Dictionary<string, ManifestEntry> _byFile = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _byFile.Keys;

    /** The entry for a file — by its name without the path, case-insensitive. */
    public ManifestEntry? For(string fileName) => _byFile.GetValueOrDefault(Path.GetFileName(fileName));

    public static Manifest Parse(string path)
    {
        // A row per component: 10 MB is thousands of rows with long descriptions. A manifest can come by link on
        // the hosted server, and a gigabyte of CSV would be read into twice as many bytes of strings.
        if (new FileInfo(path).Length > MaxBytes)
            throw new InvalidDataException($"the manifest is larger than {MaxBytes / (1024 * 1024)} MB — one line per component is far less");
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) throw new InvalidDataException("manifest is empty");

        var header = SplitCsv(lines[0]).Select(h => h.Trim().TrimStart('﻿')).ToList();
        foreach (var h in header)
            if (!Columns.Contains(h, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"unknown column \"{h}\"; allowed: {string.Join(", ", Columns)}");
        if (!header.Contains("file", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("no file column");

        var m = new Manifest();
        for (int i = 1; i < lines.Count; i++)
        {
            var cells = SplitCsv(lines[i]);
            string? Cell(string col)
            {
                int ix = header.FindIndex(h => h.Equals(col, StringComparison.OrdinalIgnoreCase));
                if (ix < 0 || ix >= cells.Count) return null;
                var v = cells[ix].Trim();
                return v.Length == 0 ? null : v;
            }

            var file = Cell("file") ?? throw new InvalidDataException($"line {i + 1}: the file field is empty");
            int? axes = null;
            var axesText = Cell("numberOfAxes");
            if (axesText != null)
            {
                if (!int.TryParse(axesText, out var n))
                    throw new InvalidDataException($"line {i + 1}: numberOfAxes \"{axesText}\" is not a number");
                axes = n;
            }
            // Axis travels are in mm, with a decimal point: in CSV the comma separates columns.
            double? Travel(string col)
            {
                var text = Cell(col);
                if (text == null) return null;
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var mm))
                    throw new InvalidDataException($"line {i + 1}: {col} \"{text}\" is not a number (mm, with a decimal point)");
                return mm;
            }
            m._byFile[Path.GetFileName(file)] = new ManifestEntry(Cell("name"), new DmcTools.FieldSet(
                null, Cell("description"),
                Cell("controllerManufacturer"), Cell("controllerSeries"), Cell("controllerModel"),
                Cell("machineManufacturer"), Cell("machineSeries"), Cell("machineModel"),
                Cell("machineType"), axes, Travel("travelXMm"), Travel("travelYMm"), Travel("travelZMm")));
        }
        return m;
    }

    /** As much of RFC 4180 as is needed: commas, quotes, doubled quotes inside. */
    internal static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells;
    }
}
