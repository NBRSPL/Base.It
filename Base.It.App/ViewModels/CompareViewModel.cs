using System.Collections.ObjectModel;
using Base.It.App.Services;
using Base.It.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Pane produced by LineAligner; one per environment that has content.
/// </summary>
public sealed record EnvPane(
    string Label,
    string? Color,
    string Definition,
    IReadOnlyList<AlignedPaneLine> Lines)
{
    /// <summary>
    /// Optional per-column summary of how this environment differs from the
    /// BASE (the first pane), set only in the N-way (3+ environment) path so
    /// each column header can flag whether — and how much — it differs. Left
    /// null in the 2-pane path, where the centred change summary already
    /// carries the count. This is what makes an "only one env differs" case
    /// visible at the column level even though the base itself is unchanged.
    /// </summary>
    public MultiDiffBadge? DiffBadge { get; set; }
}

/// <summary>Per-column diff summary vs the base pane (see <see cref="EnvPane.DiffBadge"/>).
/// <paramref name="Added"/> = lines present in this env but not the base;
/// <paramref name="Removed"/> = base lines absent here;
/// <paramref name="Changed"/> = lines present on both sides but different.</summary>
public sealed record MultiDiffBadge(bool IsBase, int Added, int Removed, int Changed)
{
    public int Total => Added + Removed + Changed;
    public bool Differs => Total > 0;
}

/// <summary>
/// Computes each environment's difference from the BASE (index 0) using the
/// exact same base-relative pair alignment the N-way diff renders, so the
/// column badges, the status line, and the highlighted body always agree.
/// One place, used by every comparison surface.
/// </summary>
public static class MultiDiffStats
{
    public static IReadOnlyList<MultiDiffBadge> Compute(
        IReadOnlyList<string> definitions, bool ignoreWhitespace)
    {
        int n = definitions.Count;
        var result = new MultiDiffBadge[n];
        result[0] = new MultiDiffBadge(IsBase: true, 0, 0, 0);
        for (int i = 1; i < n; i++)
        {
            try
            {
                var (baseLines, paneLines) =
                    LineAligner.AlignPair(definitions[0], definitions[i], ignoreWhitespace);
                int removed  = baseLines.Count(l => l.PairIndex < 0);                                  // base has, env i lacks
                int added    = paneLines.Count(l => l.PairIndex < 0);                                  // env i has, base lacks
                int changed  = baseLines.Count(l => l.PairIndex >= 0 && l.State == LineState.Different);// paired but different
                result[i] = new MultiDiffBadge(false, added, removed, changed);
            }
            catch
            {
                // Never let a diff-stat hiccup break the render; report unknown as "differs".
                result[i] = new MultiDiffBadge(false, 0, 0,
                    string.Equals(definitions[0], definitions[i], StringComparison.Ordinal) ? 0 : 1);
            }
        }
        return result;
    }
}

/// <summary>
/// Tab host for Compare: each fetch creates a new CompareTabViewModel.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly AppServices _svc;

    [ObservableProperty] private CompareTabViewModel? _activeTab;

    public ObservableCollection<CompareTabViewModel> Tabs { get; } = new();

    public CompareViewModel(AppServices svc) => _svc = svc;

    public void ReloadDatabases() { /* dropdowns now live on the FetchDock; nothing to do. */ }

    public async Task OpenTabAsync(string objectName, string database)
    {
        if (string.IsNullOrWhiteSpace(objectName) || string.IsNullOrWhiteSpace(database)) return;

        var tab = new CompareTabViewModel(_svc, objectName, database);
        Tabs.Add(tab);
        ActiveTab = tab;
        await tab.LoadAsync();
    }

    [RelayCommand]
    private void CloseTab(CompareTabViewModel? tab)
    {
        if (tab is null) return;
        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        Tabs.RemoveAt(idx);
        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(idx, Tabs.Count - 1)];
    }
}
