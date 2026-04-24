using Base.It.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Landing page. Gives the user a simple, guided view of where they are
/// and what to do next: add connections → create groups → run a sync /
/// batch / watch. Every step card is also a shortcut into the relevant tab.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _svc;

    public event Action<string>? NavigateRequested;

    [ObservableProperty] private int _connectionCount;
    [ObservableProperty] private int _groupCount;
    [ObservableProperty] private bool _hasConnections;
    [ObservableProperty] private bool _hasGroups;
    [ObservableProperty] private string _activeGroupName = "None";
    [ObservableProperty] private string _backupRoot = "";
    [ObservableProperty] private string _dacpacPath = "";
    [ObservableProperty] private bool _hasDacpac;
    [ObservableProperty] private bool _step1Done;
    [ObservableProperty] private bool _step2Done;
    [ObservableProperty] private bool _step3Enabled;

    public HomeViewModel(AppServices svc)
    {
        _svc = svc;
        Refresh();
    }

    public void Refresh()
    {
        var conns = _svc.Connections.Load();
        ConnectionCount = conns.Count;
        HasConnections  = ConnectionCount > 0;
        Step1Done       = HasConnections;

        GroupCount      = _svc.ConnectionGroups.All.Count;
        HasGroups       = GroupCount > 0;
        Step2Done       = HasGroups;

        Step3Enabled    = HasConnections;

        var active = _svc.ConnectionGroups.ActiveGroup;
        ActiveGroupName = active?.Name ?? (GroupCount == 0 ? "No groups yet" : "None selected");

        BackupRoot = _svc.Backups.Root;

        try
        {
            var opts = _svc.DacpacOptions.LoadAsync().GetAwaiter().GetResult();
            HasDacpac  = opts.Enabled && !string.IsNullOrWhiteSpace(opts.RootFolder);
            DacpacPath = HasDacpac ? opts.RootFolder : "Not configured";
        }
        catch
        {
            HasDacpac  = false;
            DacpacPath = "Unknown";
        }
    }

    [RelayCommand] private void GoSettings()  => NavigateRequested?.Invoke("Settings");
    [RelayCommand] private void GoCompare()   => NavigateRequested?.Invoke("Compare");
    [RelayCommand] private void GoSync()      => NavigateRequested?.Invoke("Sync");
    [RelayCommand] private void GoBatch()     => NavigateRequested?.Invoke("Batch");
    [RelayCommand] private void GoWatch()     => NavigateRequested?.Invoke("Watch");
    [RelayCommand] private void GoQuery()     => NavigateRequested?.Invoke("Query");
}
