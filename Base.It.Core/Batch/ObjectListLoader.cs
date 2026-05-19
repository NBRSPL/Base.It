using System.Net.Http;
using System.Text.RegularExpressions;
using NPOI.XSSF.UserModel;

namespace Base.It.Core.Batch;

/// <summary>
/// Loads a list of object names from a CSV or XLSX file, or from a URL
/// pointing at one (e.g. a published Google Sheets CSV export, a
/// SharePoint download link, or any plain HTTP(S) hosted file).
///
/// <para>The first row is treated as a header and skipped — we don't
/// require any specific column name. Every subsequent row's first
/// column is taken as the object name (case-insensitive de-duplication).
/// Other columns are ignored. Blank cells are skipped, not propagated as
/// empty rows. Same rule applies for URL-sourced sheets.</para>
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

    /// <summary>
    /// Fetch a sheet from an HTTP(S) URL and parse it with the same
    /// rules as <see cref="FromFile"/> (skip first row, take first
    /// column). Format is inferred from the URL extension first, then
    /// from the response's Content-Type. Falls back to CSV/text — which
    /// covers Google Sheets' /export?format=csv links, raw GitHub URLs,
    /// and most plain-text drops.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> when the
    /// response is HTML — the most common user error is pasting a
    /// browser-share URL (which serves the viewer page, not the data).
    /// Without this check we'd happily parse the HTML source as CSV
    /// and produce hundreds of garbage "rows".</para>
    /// </summary>
    public static async Task<IReadOnlyList<string>> FromUrlAsync(
        string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<string>();

        // Quality-of-life: if the user pasted a Google Sheets *browser* URL
        // (the one you get from the share dialog), rewrite it to the direct
        // XLSX export. Same access model — anyone-with-link view permission
        // is enough for /export — but no HTML viewer-page in the middle.
        url = NormalizeUrl(url);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Base.It/1.x (+sheet-loader)");
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";

        // Pasting a OneDrive / SharePoint share link gives back the viewer
        // HTML, not a sheet. (Google Sheets browser URLs are handled above
        // by NormalizeUrl.) Detect and refuse — otherwise we'd parse the
        // HTML source as CSV and end up with a grid full of "<div class…"
        // rows.
        if (contentType.Contains("text/html") || contentType.Contains("application/xhtml"))
        {
            throw new InvalidOperationException(
                "The URL returned an HTML page, not a sheet. The file's share permissions " +
                "may require sign-in (Base.It opens links anonymously). Re-share the file as " +
                "\"anyone with the link can view\", then paste that URL — CSV and XLSX both " +
                "work, and Google Sheets / SharePoint browser URLs are auto-rewritten to a " +
                "direct download.");
        }

        // Prefer URL extension when it's explicit. Then fall back to MIME.
        var ext = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();

        var looksXlsx = ext == ".xlsx"
            || contentType.Contains("spreadsheetml")
            || contentType.Contains("openxmlformats-officedocument")
            || contentType.Contains("ms-excel");

        if (looksXlsx)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            return FromXlsxStream(ms);
        }

        // CSV / TSV / plain text. Splitting on any line break covers
        // CRLF (Excel), LF (Unix), or CR (rare).
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Belt-and-braces: even when Content-Type lied (some servers send
        // text/plain for HTML, or no Content-Type at all), sniff the body
        // for an HTML doctype / root tag and refuse.
        var head = text.TrimStart();
        if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html",     StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The URL returned an HTML page, not a sheet. The file's share permissions " +
                "may require sign-in (Base.It opens links anonymously). Re-share the file as " +
                "\"anyone with the link can view\", then paste that URL — CSV and XLSX both " +
                "work, and Google Sheets / SharePoint browser URLs are auto-rewritten to a " +
                "direct download.");
        }

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        return FromCsvLines(lines);
    }

    /// <summary>
    /// Rewrite well-known browser / share URLs into direct download URLs:
    /// <list type="bullet">
    ///   <item>Google Sheets <c>…/spreadsheets/d/{id}/edit?…</c> →
    ///         <c>…/spreadsheets/d/{id}/export?format=xlsx</c></item>
    ///   <item>SharePoint / OneDrive share URLs (<c>…/:x:/…</c>,
    ///         <c>…/:w:/…</c>, <c>…/:b:/…</c>) → append <c>download=1</c>
    ///         so the server serves the file bytes instead of the
    ///         Office Web viewer HTML.</item>
    /// </list>
    /// Both rewrites preserve the original share token, so anyone who
    /// can view via the original link can also download via the rewritten
    /// one (no extra access required).
    /// </summary>
    internal static string NormalizeUrl(string url)
    {
        // Don't touch URLs that already opt into a download / export.
        if (url.Contains("/export?",   StringComparison.OrdinalIgnoreCase)) return url;
        if (url.Contains("download=1", StringComparison.OrdinalIgnoreCase)) return url;

        // Google Sheets browser/share URL → direct XLSX export.
        var gs = Regex.Match(
            url,
            @"^https?://docs\.google\.com/spreadsheets/d/([a-zA-Z0-9_-]+)(?:/.*)?$",
            RegexOptions.IgnoreCase);
        if (gs.Success)
        {
            var id = gs.Groups[1].Value;
            return $"https://docs.google.com/spreadsheets/d/{id}/export?format=xlsx";
        }

        // SharePoint / OneDrive share URL → force direct download.
        // The :x:/ :w:/ :b:/ etc. segment is SharePoint's "open in Office Web"
        // route; tacking ?download=1 (or &download=1 if the URL already has a
        // query) makes it return the file bytes. Tenant host varies, so we
        // match any *.sharepoint.com.
        var isSharePointShare = Regex.IsMatch(
            url,
            @"^https?://[^/]*sharepoint\.com/:[a-z]:/",
            RegexOptions.IgnoreCase);
        if (isSharePointShare)
        {
            return url.Contains('?')
                ? url + "&download=1"
                : url + "?download=1";
        }

        return url;
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
        return FromXlsxStream(fs);
    }

    private static IReadOnlyList<string> FromXlsxStream(Stream stream)
    {
        var wb = new XSSFWorkbook(stream);
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
