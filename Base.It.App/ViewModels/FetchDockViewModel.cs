using System.Collections.ObjectModel;
using Base.It.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Global pinned/expandable fetch bar that lives in the main window.
/// Routes fetches to the Compare page (new tab per fetch).
/// </summary>
public sealed partial class FetchDockViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Func<string, string, Task> _onFetch;

    [ObservableProperty] private string _objectName = "";
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private bool _isExpanded = true;   // pinned/expanded by default
    [ObservableProperty] private bool _isVisible = true;    // hidden on Settings
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<string> Databases { get; } = new();

    public FetchDockViewModel(AppServices svc, Func<string, string, Task> onFetch)
    {
        _svc = svc;
        _onFetch = onFetch;
        ReloadDatabases();
    }

    public void ReloadDatabases()
    {
        var current = SelectedDatabase;
        Databases.Clear();
        foreach (var d in EnvironmentListProvider.Databases(_svc)) Databases.Add(d);
        SelectedDatabase = Databases.Contains(current ?? "") ? current
            : (Databases.Count > 0 ? Databases[0] : null);
    }

    [RelayCommand] private void Toggle()   => IsExpanded = !IsExpanded;
    [RelayCommand] private void Expand()   => IsExpanded = true;
    [RelayCommand] private void Collapse() => IsExpanded = false;

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (string.IsNullOrWhiteSpace(ObjectName))
        {
            _svc.Toasts.Warning("Missing object", "Enter an object name before fetching.");
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedDatabase))
        {
            _svc.Toasts.Warning("No database picked", "Pick a database from the fetch dock first.");
            return;
        }
        IsBusy = true;
        try
        {
            await _onFetch(ObjectName.Trim(), SelectedDatabase!);
        }
        catch (Exception ex)
        {
            _svc.Toasts.Error("Fetch failed", ex.Message);
        }
        finally { IsBusy = false; }
    }
}
