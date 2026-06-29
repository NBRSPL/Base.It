using System.Collections.Generic;

namespace Base.It.App.ViewModels;

/// <summary>
/// Implemented by any grid / list ViewModel that can hand its current
/// (filtered + sorted) rows to <see cref="Services.CsvExport"/>. Keeps the
/// View's "Export CSV" handler dumb: it just resolves the DataContext to
/// this interface and forwards to the shared saver.
/// </summary>
public interface ICsvExportable
{
    /// <summary>Default file name offered in the save dialog (no extension required).</summary>
    string CsvSuggestedFileName { get; }

    /// <summary>Column headers, in display order.</summary>
    IReadOnlyList<string> CsvHeaders { get; }

    /// <summary>
    /// Current rows in the order the grid shows them — same filtering and
    /// sorting the user sees. Each row's cell count must match
    /// <see cref="CsvHeaders"/>.
    /// </summary>
    IEnumerable<IReadOnlyList<string?>> CsvRows();

    /// <summary>False when there is nothing to export (drives the empty-state toast / disabled button).</summary>
    bool HasExportableRows { get; }
}
