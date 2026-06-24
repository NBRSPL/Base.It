using Avalonia.Controls;

namespace Base.It.App.Services;

/// <summary>
/// Shared Ctrl+C handler for the app's data grids. Copies the
/// FullName of each selected row to the clipboard, one per line —
/// the exact shape Batch's paste-from-clipboard fan-out expects, so
/// the user can copy from any grid and paste straight into Batch.
///
/// Centralised here rather than open-coded in each view because:
///   • Three grids (Snapshot Entries / Recent Changes / Compare diff)
///     in SnapshotsView plus the Batch items grid would otherwise
///     duplicate the clipboard plumbing and selection-fallback rule.
///   • The rule "prefer ticked rows over highlighted rows" is a single
///     UX decision that belongs in one place; if we change it later
///     we change it once.
/// </summary>
public static class GridCopyHelper
{
    /// <summary>
    /// Resolve which rows the user meant to copy:
    ///   • If any rows in <paramref name="tickedItems"/> have a non-empty
    ///     FullName, those win — the user has explicitly curated a set
    ///     via the checkbox column.
    ///   • Otherwise fall back to <paramref name="highlightedItems"/> from
    ///     the grid's <c>SelectedItems</c> (Extended-mode shift/ctrl-click).
    /// Skipping the ticked path entirely when <paramref name="tickedItems"/>
    /// is null lets grids without a check column (Snapshot Entries) reuse
    /// the same helper.
    /// </summary>
    /// <returns>Number of names actually copied. Zero when there's nothing
    /// to copy or the visual tree doesn't expose a clipboard.</returns>
    public static async Task<int> CopyFullNamesAsync<T>(
        TopLevel?            top,
        IEnumerable<T>?      tickedItems,
        IEnumerable<T>       highlightedItems,
        Func<T, string?>     getFullName)
        where T : class
    {
        var clipboard = top?.Clipboard;
        if (clipboard is null) return 0;

        // Ticked rows win when present — they're the user's explicit pick.
        // Falling back to highlighted lets grids without checkboxes
        // (Snapshot Entries) still benefit from Ctrl+C.
        var picked = tickedItems?.ToList() ?? new List<T>();
        if (picked.Count == 0) picked = highlightedItems?.ToList() ?? new List<T>();
        if (picked.Count == 0) return 0;

        var names = picked
            .Select(getFullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return 0;

        await clipboard.SetTextAsync(string.Join(Environment.NewLine, names));
        return names.Count;
    }
}
