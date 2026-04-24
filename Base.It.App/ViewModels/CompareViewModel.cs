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
    IReadOnlyList<AlignedPaneLine> Lines);

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
