using System.Collections.ObjectModel;
using Base.It.App.Services;
using Base.It.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public AppServices Services { get; }
    public FetchDockViewModel FetchDock { get; }
    public HomeViewModel      Home      { get; }
    public CompareViewModel   Compare   { get; }
    public SyncViewModel      Sync      { get; }
    public BatchViewModel     Batch     { get; }
    public QueryViewModel     Query     { get; }
    public WatchViewModel     Watch     { get; }
    public SettingsViewModel  Settings  { get; }

    public bool HasAnyConnection => Services.Connections.Load().Count > 0;

    /// <summary>Bound by the top-bar active-group picker. Also exposed in Settings.</summary>
    public ObservableCollection<ConnectionGroup> ConnectionGroups { get; } = new();

    [ObservableProperty] private ConnectionGroup? _activeConnectionGroup;

    /// <summary>Raised when the dock wants Compare foreground + a new tab.</summary>
    public event Action? NavigateToCompareRequested;

    /// <summary>Raised by the Watch pane's "Send Changed to Batch" action.</summary>
    public event Action? NavigateToBatchRequested;

    /// <summary>Raised by the Home pane when a shortcut card is clicked.</summary>
    public event Action<string>? NavigateToTagRequested;

    public MainWindowViewModel()
    {
        Services = new AppServices();
        Compare  = new CompareViewModel(Services);
        Sync     = new SyncViewModel(Services);
        Batch    = new BatchViewModel(Services);
        Query    = new QueryViewModel(Services);
        Watch    = new WatchViewModel(Services);
        Settings = new SettingsViewModel(Services);
        Home     = new HomeViewModel(Services);
        Home.NavigateRequested += tag => NavigateToTagRequested?.Invoke(tag);
        FetchDock = new FetchDockViewModel(Services, async (obj, db) =>
        {
            NavigateToCompareRequested?.Invoke();
            await Compare.OpenTabAsync(obj, db);
        });

        // Load persisted connection groups + apply the persisted active pointer.
        _ = LoadGroupsAsync();

        // Warm up every configured connection on a background thread so
        // the first real Sync / Compare / Query doesn't pay the SQL
        // cold-start cost (TLS handshake + auth + pool creation). Fire
        // and forget — if a connection is unreachable the user still
        // sees the real error on their first actual use.
        _ = Services.WarmUpConnectionsAsync();

        Settings.ConnectionsChanged += () =>
        {
            FetchDock.ReloadDatabases();
            Sync.Reload();
            Batch.Reload();
            _ = Batch.RefreshDacpacAvailabilityAsync();
            _ = Sync.RefreshDacpacAvailabilityAsync();
            _ = Watch.RefreshDacpacAvailabilityAsync();
            Query.Reload();
            Home.Refresh();
            OnPropertyChanged(nameof(HasAnyConnection));
        };

        Settings.ConnectionGroupsChanged += async () =>
        {
            await LoadGroupsAsync();
            Home.Refresh();
        };

        // When the active group flips, reload everyone using the env list.
        Services.ActiveConnectionGroupChanged += () =>
        {
            FetchDock.ReloadDatabases();
            Sync.Reload();
            Batch.Reload();
            Query.Reload();
        };

        Watch.SendToBatchRequested += (names, srcEnv, tgtEnv, db) =>
        {
            Batch.Items.Clear();
            foreach (var n in names) Batch.Items.Add(new BatchItem(n));
            Batch.SourceEnv = srcEnv;
            Batch.Database  = db;
            Batch.TargetEnv = tgtEnv;
            Batch.Status    = $"Loaded {names.Count} object(s) from watch group.";
            NavigateToBatchRequested?.Invoke();
        };
    }

    /// <summary>
    /// Reload groups from disk and reconcile the top-bar picker with the
    /// persisted active id. Silent — called at startup and whenever the
    /// Settings pane saves a change to the groups.
    /// </summary>
    public async Task LoadGroupsAsync()
    {
        // Bootstrap: first run with configured connections but no groups
        // gets a "Default" group auto-created; orphan connections get
        // adopted into Default so everything stays discoverable.
        await Services.EnsureDefaultConnectionGroupAsync();
        ConnectionGroups.Clear();
        foreach (var g in Services.ConnectionGroups.All) ConnectionGroups.Add(g);
        ActiveConnectionGroup = Services.ConnectionGroups.ActiveGroup;
    }

    /// <summary>
    /// Generated partial hook — fires when the top-bar combo selection
    /// flips. Persists the new pointer via the service and lets the
    /// ActiveConnectionGroupChanged handler do the VM refresh.
    /// </summary>
    async partial void OnActiveConnectionGroupChanged(ConnectionGroup? value)
    {
        await Services.SetActiveConnectionGroupAsync(value?.Id);
    }

    /// <summary>Clear the active-group filter — all connections become visible again.</summary>
    [RelayCommand]
    private void ClearActiveGroup() => ActiveConnectionGroup = null;
}
