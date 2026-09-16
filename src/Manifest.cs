using System.Text;

namespace DmcMcp;

/** Строка manifest: имя (если задано) и поля карточки для одного файла. */
internal sealed record ManifestEntry(string? Name, DmcTools.FieldSet Fields);

/**
 * CSV автора рядом с постами — точные данные вместо догадок ИИ:
 * <c>file,name,controllerManufacturer,…</c>. Обязательна только колонка <c>file</c>.
 * Неизвестная колонка — ошибка, а не молчание: опечатка в заголовке иначе тихо выбросила бы
 * целый столбец, и автор узнал бы об этом по карточкам.
 */
internal sealed class Manifest
{
    internal static readonly string[] Columns =
    {
        "file", "name", "description", "controllerManufacturer", "controllerSeries", "controllerModel",
        "machineManufacturer", "machineSeries", "machineModel", "machineType", "numberOfAxes",
    };

    private readonly Dictionary<string, ManifestEntry> _byFile = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _byFile.Keys;

    /** Запись для файла — по имени без пути, регистр не важен. */
    public ManifestEntry? For(string fileName) => _byFile.GetValueOrDefault(Path.GetFileName(fileName));

    public static Manifest Parse(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) throw new InvalidDataException("manifest пуст");

        var header = SplitCsv(lines[0]).Select(h => h.Trim().TrimStart('﻿')).ToList();
        foreach (var h in header)
            if (!Columns.Contains(h, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"неизвестная колонка «{h}»; допустимы: {string.Join(", ", Columns)}");
        if (!header.Contains("file", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("нет колонки file");

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

            var file = Cell("file") ?? throw new InvalidDataException($"строка {i + 1}: пустое поле file");
            int? axes = null;
            var axesText = Cell("numberOfAxes");
            if (axesText != null)
            {
                if (!int.TryParse(axesText, out var n))
                    throw new InvalidDataException($"строка {i + 1}: numberOfAxes «{axesText}» — не число");
                axes = n;
            }
            m._byFile[Path.GetFileName(file)] = new ManifestEntry(Cell("name"), new DmcTools.FieldSet(
                null, Cell("description"),
                Cell("controllerManufacturer"), Cell("controllerSeries"), Cell("controllerModel"),
                Cell("machineManufacturer"), Cell("machineSeries"), Cell("machineModel"),
                Cell("machineType"), axes));
        }
        return m;
    }

    /** RFC 4180 в объёме, которого хватает: запятые, кавычки, удвоенные кавычки внутри. */
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
