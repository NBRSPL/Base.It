using NPOI.XSSF.UserModel;

namespace Base.It.Core.Batch;

/// <summary>
/// Loads a list of object names from a CSV or XLSX file. The file must have
/// a header row containing a column named "Object name" (case-insensitive).
/// Other columns are ignored. Matches the original DB_Sync behaviour.
/// </summary>
public static class ObjectListLoader
{
    public static IReadOnlyList<string> FromFile(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv"  => FromCsvLines(File.ReadAllLines(path)),
            ".xlsx" => FromXlsx(path),
            _       => Array.Empty<string>()
        };
    }

    public static IReadOnlyList<string> FromCsvLines(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2) return Array.Empty<string>();
        var headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToArray();
        var col = Array.FindIndex(headers,
            h => h.Equals("Object name", StringComparison.OrdinalIgnoreCase));
        if (col < 0) return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Count; i++)
        {
            var cells = lines[i].Split(',').Select(c => c.Trim().Trim('"')).ToArray();
            if (col < cells.Length)
            {
                var name = cells[col];
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
            }
        }
        return result;
    }

    private static IReadOnlyList<string> FromXlsx(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var wb = new XSSFWorkbook(fs);
        var sheet = wb.GetSheetAt(0);
        var header = sheet.GetRow(0);
        if (header is null) return Array.Empty<string>();

        int col = -1;
        for (int c = 0; c < header.LastCellNum; c++)
        {
            var v = header.GetCell(c)?.ToString()?.Trim();
            if (v is not null && v.Equals("Object name", StringComparison.OrdinalIgnoreCase)) { col = c; break; }
        }
        if (col < 0) return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 1; r <= sheet.LastRowNum; r++)
        {
            var cell = sheet.GetRow(r)?.GetCell(col);
            var name = cell?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name!)) result.Add(name!);
        }
        return result;
    }
}
