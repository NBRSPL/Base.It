using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Base.It.App.ViewModels;

namespace Base.It.App.Services;

/// <summary>
/// Shared CSV export used by every grid / list view. Building the text is
/// pure (RFC-4180 escaping); saving is the View's job because only the View
/// can reach Avalonia's <see cref="IStorageProvider"/> via its TopLevel.
///
/// A grid's ViewModel implements <see cref="ICsvExportable"/> to describe
/// its current (filtered + sorted) rows; the View's "Export CSV" button
/// handler calls <see cref="SaveAsync"/> with the owning control.
/// </summary>
public static class CsvExport
{
    /// <summary>
    /// Build RFC-4180 CSV text. A field is quoted only when it contains a
    /// comma, double-quote, CR, or LF; embedded quotes are doubled. Always
    /// CRLF line endings so Excel on Windows reads it cleanly.
    /// </summary>
    public static string Build(IReadOnlyList<string> headers,
                               IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, headers);
        foreach (var row in rows) AppendRow(sb, row);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string?> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(fields[i]));
        }
        sb.Append("\r\n");
    }

    private static string Escape(string? field)
    {
        var s = field ?? "";
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Show a save dialog and write the exportable's current rows to the
    /// chosen .csv file. No-ops with a toast when the grid is empty or the
    /// user cancels. Writes a UTF-8 BOM so Excel auto-detects encoding.
    /// </summary>
    public static async Task SaveAsync(Visual? owner, ICsvExportable exportable, ToastService? toasts)
    {
        if (exportable is null) return;

        if (!exportable.HasExportableRows)
        {
            toasts?.Info("Nothing to export", "There are no rows in this view yet.");
            return;
        }

        var top = owner is null ? null : TopLevel.GetTopLevel(owner);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Export to CSV",
            SuggestedFileName      = exportable.CsvSuggestedFileName,
            DefaultExtension       = "csv",
            ShowOverwritePrompt    = true,
            FileTypeChoices        = new[]
            {
                new FilePickerFileType("CSV (comma-separated)") { Patterns = new[] { "*.csv" } },
            },
        });
        if (file is null) return;

        await WriteAsync(file, Build(exportable.CsvHeaders, exportable.CsvRows()), toasts);
    }

    /// <summary>
    /// Direct overload for views that host more than one grid (so a single
    /// <see cref="ICsvExportable"/> on the VM isn't enough). Caller supplies
    /// the headers + rows for the specific grid being exported.
    /// </summary>
    public static async Task SaveAsync(Visual? owner, string suggestedFileName,
                                       IReadOnlyList<string> headers,
                                       IEnumerable<IReadOnlyList<string?>> rows,
                                       bool hasRows, ToastService? toasts)
    {
        if (!hasRows)
        {
            toasts?.Info("Nothing to export", "There are no rows in this view yet.");
            return;
        }

        var top = owner is null ? null : TopLevel.GetTopLevel(owner);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Export to CSV",
            SuggestedFileName   = suggestedFileName,
            DefaultExtension    = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices     = new[]
            {
                new FilePickerFileType("CSV (comma-separated)") { Patterns = new[] { "*.csv" } },
            },
        });
        if (file is null) return;

        await WriteAsync(file, Build(headers, rows), toasts);
    }

    private static async Task WriteAsync(IStorageFile file, string csv, ToastService? toasts)
    {
        try
        {
            await using var stream = await file.OpenWriteAsync();
            // OpenWriteAsync does NOT truncate — overwriting a longer file
            // would otherwise leave stale trailing bytes. Reset length first.
            if (stream.CanSeek) stream.SetLength(0);
            // UTF-8 *with* BOM: Excel needs the BOM to read non-ASCII
            // object names (accented schema names etc.) correctly.
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteAsync(csv);
            toasts?.Success("Exported", $"Saved {file.Name}.");
        }
        catch (Exception ex)
        {
            toasts?.Error("Export failed", ex.Message);
        }
    }
}
