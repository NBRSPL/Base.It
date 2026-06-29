using System.Collections.Generic;
using System.Linq;

namespace Base.It.App.ViewModels;

public enum SortDir { None, Asc, Desc }

/// <summary>
/// Tracks click-to-sort state for a custom-header grid (the app renders its
/// own header row with <c>HeadersVisibility=None</c>, so DataGrid's built-in
/// sorting isn't available). One instance per grid. Clicking a column cycles
/// it Asc → Desc → None, matching the existing Snapshots sort UX.
/// </summary>
public sealed class ColumnSorter
{
    public string? ActiveKey { get; private set; }
    public SortDir Direction { get; private set; } = SortDir.None;

    /// <summary>Cycle the given column: a new column starts Asc; the active one goes Asc → Desc → None.</summary>
    public void Toggle(string key)
    {
        if (!string.Equals(ActiveKey, key))
        {
            ActiveKey = key;
            Direction = SortDir.Asc;
            return;
        }
        Direction = Direction switch
        {
            SortDir.Asc  => SortDir.Desc,
            SortDir.Desc => SortDir.None,
            _            => SortDir.Asc,
        };
        if (Direction == SortDir.None) ActiveKey = null;
    }

    /// <summary>Arrow shown next to a column label: ▲ / ▼ on the active column, blank otherwise.</summary>
    public string Indicator(string key)
    {
        if (!string.Equals(ActiveKey, key)) return "";
        return Direction switch
        {
            SortDir.Asc  => " ▲",
            SortDir.Desc => " ▼",
            _            => "",
        };
    }

    /// <summary>
    /// Apply the current sort to <paramref name="src"/> using the keyed
    /// selectors. When no column is active the input order is preserved.
    /// </summary>
    public IEnumerable<T> Apply<T>(IEnumerable<T> src,
                                   IReadOnlyDictionary<string, System.Func<T, object?>> selectors)
    {
        if (ActiveKey is null || Direction == SortDir.None ||
            !selectors.TryGetValue(ActiveKey, out var sel))
            return src;

        return Direction == SortDir.Asc
            ? src.OrderBy(sel, NaturalComparer.Instance)
            : src.OrderByDescending(sel, NaturalComparer.Instance);
    }
}

/// <summary>
/// Comparer that orders strings case-insensitively and numbers numerically,
/// so a "Size" column sorts by value rather than lexicographically. Nulls
/// sort first.
/// </summary>
internal sealed class NaturalComparer : IComparer<object?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (x is string sx && y is string sy)
            return string.Compare(sx, sy, System.StringComparison.OrdinalIgnoreCase);

        if (x is IComparable cx && x.GetType() == y.GetType())
            return cx.CompareTo(y);

        return string.Compare(x.ToString(), y.ToString(), System.StringComparison.OrdinalIgnoreCase);
    }
}
