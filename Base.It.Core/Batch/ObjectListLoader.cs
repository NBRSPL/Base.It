using NPOI.XSSF.UserModel;

namespace Base.It.Core.Batch;

/// <summary>
/// Loads a list of object names from a CSV or XLSX file.
///
/// <para>The first row is treated as a header and skipped — we don't
/// require any specific column name. Every subsequent row's first
/// column is taken as the object name (case-insensitive de-duplication).
/// Other columns are ignored. Blank cells are skipped, not propagated as
/// empty rows.</para>
///
/// <para>This is permissive on purpose: the original loader only worked
/// when the spreadsheet had an exact "Object name" header column, which
/// silently dropped real input files that just happened to call the
/// column something else (Name, ObjectName, Procedure, etc.).</para>
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

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Skip lines[0] — header row, contents irrelevant.
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Take the first cell. Strip surrounding quotes the way a
            // CSV roundtrip from Excel produces them.
            var firstComma = line.IndexOf(',');
            var raw = firstComma < 0 ? line : line.Substring(0, firstComma);
            var name = raw.Trim().Trim('"').Trim();
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
        }
        return result;
    }

    private static IReadOnlyList<string> FromXlsx(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var wb = new XSSFWorkbook(fs);
        var sheet = wb.GetSheetAt(0);
        if (sheet.LastRowNum < 1) return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Skip row 0 — header row, contents irrelevant.
        for (int r = 1; r <= sheet.LastRowNum; r++)
        {
            var cell = sheet.GetRow(r)?.GetCell(0);
            var name = cell?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name!)) result.Add(name!);
        }
        return result;
    }
}
