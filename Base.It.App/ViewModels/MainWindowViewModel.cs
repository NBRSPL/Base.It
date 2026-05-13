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
    public ScriptsViewModel   Scripts   { get; }
    public QueryViewModel     Query     { get; }
    public WatchViewModel     Watch     { get; }
    public SettingsViewModel  Settings  { get; }

    public bool HasAnyConnection => Services.Connections.Load().Count > 0;

    /// <summary>Bound by the top-bar active-group picker. Also exposed in Settings.</summary>
    public ObservableCollection<ConnectionGroup> ConnectionGroups { get; } = new();

    /// <summary>
    /// Flat picker source for the title-bar connection-group AutoCompleteBox.
    /// Always begins with the synthetic "All connections" entry (Group=null)
    /// so the user has a single click to drop the filter, plus one
    /// <see cref="ConnectionGroupOption"/> per real group. Rebuilt by
    /// <see cref="LoadGroupsAsync"/>.
    /// </summary>
    public ObservableCollection<ConnectionGroupOption> ConnectionGroupOptions { get; } = new();

    [ObservableProperty] private ConnectionGroup? _activeConnectionGroup;
    [ObservableProperty] private ConnectionGroupOption? _selectedConnectionGroupOption;

    /// <summary>Raised when the dock wants Compare foreground + a new tab.</summary>
    public event Action? NavigateToCompareRequested;

    /// <summary>Raised by the Watch pane's "Send Changes to Batch" action.</summary>
    public event Action? NavigateToBatchRequested;

    /// <summary>Raised by the Home pane when a shortcut card is clicked.</summary>
    public event Action<string>? NavigateToTagRequested;

    public MainWindowViewModel()
    {
        Services = new AppServices();
        Compare  = new CompareViewModel(Services);
        Sync     = new SyncViewModel(Services);
        Batch    = new BatchViewModel(Services);
        Scripts  = new ScriptsViewModel(Services);
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
            Scripts.Reload();
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
            Scripts.Reload();
            Query.Reload();
        };

        Watch.SendToBatchRequested += payload =>
        {
            _ = HandleSendToBatchAsync(payload);
        };
    }

    /// <summary>
    /// Raised when "Send Changes to Batch" wants to open a freshly-populated
    /// Batch in its OWN window so the user can preserve whatever state the
    /// main Batch tab currently holds. MainWindow subscribes and instantiates
    /// the actual <see cref="Views.BatchWindow"/>. Keeping Window creation out
    /// of the VM avoids dragging Avalonia's Window type into the model layer.
    /// </summary>
    public event Action<BatchViewModel>? OpenBatchInNewWindowRequested;

    /// <summary>
    /// Decide what to do when Watch hands off a list to Batch:
    ///   1. If the main Batch tab is empty (no pending items) → just populate
    ///      it and navigate. No dialog, no friction.
    ///   2. Otherwise → ask the user whether to Replace, open a new window,
    ///      or Cancel. Default-focused button is Cancel so an accidental
    ///      Enter doesn't blow away an in-progress batch.
    /// </summary>
    private async Task HandleSendToBatchAsync(SendToBatchPayload payload)
    {
        bool replaceMain;
        if (Batch.Items.Count == 0)
        {
            // No existing state — fall straight through, no confusing prompt.
            replaceMain = true;
        }
        else
        {
            var choice = await ChoiceDialog.AskAsync(
                title:         "Batch already has rows",
                message:       $"The main Batch tab currently has {Batch.Items.Count} row(s). " +
                               $"Sending {payload.ObjectNames.Count} object(s) from Watch — what do you want to do?",
                primaryText:   "Open in new window",
                secondaryText: "Replace current",
                cancelText:    "Cancel");
            switch (choice)
            {
                case ChoiceDialogResult.Primary:
                    // Build a fresh BatchViewModel + open it in its own window.
                    // The main tab is left exactly as it was.
                    var fresh = new BatchViewModel(Services);
                    PopulateBatch(fresh, payload);
                    OpenBatchInNewWindowRequested?.Invoke(fresh);
                    return;
                case ChoiceDialogResult.Secondary:
                    replaceMain = true;
                    break;
                default:
                    return; // Cancel — leave everything alone.
            }
        }

        if (replaceMain)
        {
            // Navigate FIRST so Batch.Reload() runs cleanly, then layer the
            // sent state on top — same reasoning as before.
            NavigateToBatchRequested?.Invoke();
            PopulateBatch(Batch, payload);
        }
    }

    /// <summary>
    /// Shared population helper — used for both "replace main tab" and
    /// "open in new window". Mirrors the FULL watch-group configuration:
    /// source endpoint, source database, AND every target route. Each
    /// matching target chip in the destination Batch gets ticked so the
    /// recipient is one Execute click away from running. Filters reset so
    /// a stale "Success/Skipped" filter doesn't hide the freshly-sent
    /// rows (their default Status is Pending).
    /// </summary>
    private static void PopulateBatch(BatchViewModel batch, SendToBatchPayload payload)
    {
        batch.Items.Clear();
        foreach (var n in payload.ObjectNames) batch.Items.Add(new BatchItem(n));

        batch.SourceEnv    = payload.SourceEnv;
        batch.Database     = payload.SourceDatabase;

        // Untick every existing target first, then re-tick exactly the
        // ones the watch group was monitoring. Match by (env, database)
        // case-insensitively so casing drift between Settings and Watch
        // doesn't drop a target.
        foreach (var t in batch.Targets) t.IsChecked = false;
        foreach (var (env, db) in payload.Targets)
        {
            var pick = batch.Targets.FirstOrDefault(t =>
                string.Equals(t.Environment, env, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Database,    db,  StringComparison.OrdinalIgnoreCase));
            if (pick is not null) pick.IsChecked = true;
        }

        batch.StatusFilter = "All";
        batch.NameFilter   = "";
        batch.Status       = $"Loaded {payload.ObjectNames.Count} object(s) from watch group, {payload.Targets.Count} target(s) ticked.";
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

        RebuildConnectionGroupOptions();
    }

    /// <summary>
    /// Reflects the current <see cref="ConnectionGroups"/> + active id
    /// into <see cref="ConnectionGroupOptions"/> with "All connections"
    /// pinned at index 0. Selection is preserved by id.
    /// </summary>
    private void RebuildConnectionGroupOptions()
    {
        ConnectionGroupOptions.Clear();
        ConnectionGroupOptions.Add(ConnectionGroupOption.All);
        foreach (var g in ConnectionGroups)
            ConnectionGroupOptions.Add(new ConnectionGroupOption(g.Name, g));

        var match = ConnectionGroupOptions.FirstOrDefault(o => o.Group?.Id == ActiveConnectionGroup?.Id)
                    ?? ConnectionGroupOptions[0];
        if (!ReferenceEquals(SelectedConnectionGroupOption, match))
            SelectedConnectionGroupOption = match;
    }

    /// <summary>
    /// Generated partial hook — fires when the top-bar combo selection
    /// flips. Persists the new pointer via the service and lets the
    /// ActiveConnectionGroupChanged handler do the VM refresh.
    /// </summary>
    async partial void OnActiveConnectionGroupChanged(ConnectionGroup? value)
    {
        await Services.SetActiveConnectionGroupAsync(value?.Id);
        // Mirror to the AutoCompleteBox-bound option so the picker
        // text stays in sync when something else changes the active
        // group (e.g. Settings save, Home shortcut).
        var match = ConnectionGroupOptions.FirstOrDefault(o => o.Group?.Id == value?.Id);
        if (match is not null && !ReferenceEquals(SelectedConnectionGroupOption, match))
            SelectedConnectionGroupOption = match;
    }

    /// <summary>
    /// Two-way pull from the AutoCompleteBox. Picking "All connections"
    /// (Group=null) clears the filter; picking a real group sets it.
    /// </summary>
    partial void OnSelectedConnectionGroupOptionChanged(ConnectionGroupOption? value)
    {
        if (value is null) return;
        if (ReferenceEquals(value.Group, ActiveConnectionGroup)) return;
        ActiveConnectionGroup = value.Group;
    }

    /// <summary>Clear the active-group filter — all connections become visible again.</summary>
    [RelayCommand]
    private void ClearActiveGroup() => ActiveConnectionGroup = null;
}
