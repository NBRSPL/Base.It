using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Base.It.App.Services;
using Base.It.Core.Batch;
using Base.It.Core.Config;
using Base.It.Core.Dacpac;
using Base.It.Core.Models;
using Base.It.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

public enum BatchStatus { Pending, Running, Success, Failed, Skipped }

/// <summary>
/// Where Batch reads object SQL from at Execute time.
/// <list type="bullet">
///   <item><b>Live</b> — fetches via the source endpoint's connection
///         string (the original 1.x behaviour).</item>
///   <item><b>Snapshot</b> — reads from the picked snapshot's local
///         schema store. Reproducible: even if the live source changes
///         between selecting a snapshot and clicking Execute, the SQL
///         that runs is exactly what was captured.</item>
/// </list>
/// </summary>
public enum BatchSourceMode { Live, Snapshot }

/// <summary>
/// One row in the Batch source picker. Wraps a live endpoint or a
/// (live endpoint + snapshot) pair so the picker can list both kinds
/// in a single dropdown.
///
/// Identity: live items use the endpoint's <c>Key</c>; snapshot items
/// use the same plus the snapshot id, so two snapshots of the same
/// endpoint are distinct picker entries.
/// </summary>
public sealed record BatchSourceItem(
    EndpointPick Endpoint,
    Base.It.Core.Schema.SnapshotSummary? Snapshot)
{
    public bool IsSnapshot => Snapshot is not null;

    /// <summary>Primary label rendered in the dropdown row.</summary>
    public string Label => IsSnapshot
        ? $"{Endpoint.Label} @ {Snapshot!.DisplayName}"
        : Endpoint.Label;

    /// <summary>Subtitle line under the label — kind + (for snapshots) the underlying env/db pair.</summary>
    public string SubLabel => IsSnapshot
        ? $"snapshot · {Endpoint.SubLabel}"
        : Endpoint.SubLabel;

    public string? Color => Endpoint.Color;

    public string Key => IsSnapshot
        ? $"snap|{Endpoint.Key}|{Snapshot!.Id}"
        : $"live|{Endpoint.Key}";

    public override string ToString() => Label;
}

public sealed partial class BatchItem : ObservableObject
{
    [ObservableProperty] private bool        _isSelected;
    [ObservableProperty] private int         _index;
    [ObservableProperty] private string      _name    = "";
    [ObservableProperty] private BatchStatus _status  = BatchStatus.Pending;
    [ObservableProperty] private string      _message = "";
    public BatchItem(string name) { _name = name; }

    /// <summary>Drives the inline "View" button visibility: only failed rows expose their full error.</summary>
    public bool HasError => Status == BatchStatus.Failed && !string.IsNullOrWhiteSpace(Message);

    partial void OnStatusChanged(BatchStatus value)  => OnPropertyChanged(nameof(HasError));
    partial void OnMessageChanged(string value)      => OnPropertyChanged(nameof(HasError));

    /// <summary>
    /// Lets the row's Button-styled-as-checkbox toggle IsSelected
    /// directly — same primitive the column header uses. We can't use a
    /// real CheckBox there because Avalonia DataGrid won't render one in
    /// a column header, so we standardize on Buttons everywhere for
    /// pixel-perfect alignment between header and rows.
    /// </summary>
    [RelayCommand]
    private void ToggleSelection() => IsSelected = !IsSelected;
}

/// <summary>
/// Multi-target batch push. Source is a single (env, db); targets are a
/// ticked list. Execution iterates items × selected targets and reports
/// per-target outcomes in the <see cref="BatchItem.Message"/> field;
/// <see cref="BatchItem.Status"/> is the worst-of-all aggregate.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private readonly AppServices _svc;

    [ObservableProperty] private string? _sourceEnv;
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _statusFilter = "All";
    /// <summary>Name-substring filter, intersects with <see cref="StatusFilter"/>.</summary>
    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private int _successCount;
    [ObservableProperty] private int _failCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "No objects loaded.";
    [ObservableProperty] private EnvironmentConfig? _sourceProfile;

    // UI alias for the source — backed by SourceEnv + SourceDatabase. Kept
    // in sync via _syncingEndpoint so neither side recurses into the other.
    [ObservableProperty] private EndpointPick? _selectedSourceEndpoint;
    [ObservableProperty] private EndpointProfile? _selectedProfile;
    [ObservableProperty] private string _targetFilter = "";

    private bool _syncingEndpoint;
    private bool _suspendRebuild;

    // DACPAC per-run opt-in.
    [ObservableProperty] private bool _stageAsDacpacBranch;
    [ObservableProperty] private bool _dacpacConfigured;

    // Optional user-supplied label for this run's backup folder. When empty,
    // FileBackupStore generates a millisecond timestamp like "HHmmssfff". When
    // set, the user's label becomes the folder prefix (e.g.
    // "before-feature-x_source_DEV") so the run is easy to find later.
    // Populated by the popup prompt in Backup/Execute when UseAutoBackupName
    // is unticked; cleared at the end of every run.
    [ObservableProperty] private string _customBackupName = "";
    [ObservableProperty] private string _customBackupNameError = "";

    /// <summary>
    /// When true (default), Backup / Execute uses a millisecond timestamp
    /// for the run-folder name. When false, the user is prompted on each
    /// click for a custom label so they can find specific runs later
    /// (e.g. "before-feature-x").
    /// </summary>
    [ObservableProperty] private bool _useAutoBackupName = true;

    // Seeds StageAsDacpacBranch from settings once; prevents later refreshes
    // (e.g. after Settings "Save All") from clobbering the user's toggle.
    private bool _dacpacDefaultsApplied;

    public ObservableCollection<string>        Environments    { get; } = new();
    public ObservableCollection<string>        Databases       { get; } = new();
    public ObservableCollection<TargetPickVm>  Targets         { get; } = new();
    public ObservableCollection<BatchItem>     Items           { get; } = new();
    public ObservableCollection<BatchItem>     FilteredItems   { get; } = new();

    public ObservableCollection<EndpointPick>     Endpoints       { get; } = new();
    public ObservableCollection<EndpointProfile>  Profiles        { get; } = new();
    public ObservableCollection<TargetPickVm>     FilteredTargets { get; } = new();

    /// <summary>
    /// Endpoints minus every ticked target — what the SOURCE picker should
    /// show. You can't sync from a target to itself, so once a target is
    /// ticked it should disappear from the source dropdown. Recomputed
    /// whenever the source or any target's IsChecked changes.
    /// </summary>
    public ObservableCollection<EndpointPick> SourceCandidateEndpoints { get; } = new();

    /// <summary>
    /// Endpoints minus the source and minus every ticked target — what
    /// the "Add target" picker should show. Avoids the user picking the
    /// source as a target, or picking the same target twice.
    /// </summary>
    public ObservableCollection<EndpointPick> TargetCandidateEndpoints { get; } = new();

    /// <summary>
    /// Unified source picker items: live endpoints + every snapshot of
    /// every endpoint's local schema store. The Batch source dropdown
    /// binds to this so the user can pick either a live source (Execute
    /// fetches fresh SQL at run time) or a snapshot source (Execute
    /// replays the stored SQL — reproducible).
    /// </summary>
    public ObservableCollection<BatchSourceItem> SourceCandidates { get; } = new();

    /// <summary>
    /// Selected item from <see cref="SourceCandidates"/>. Setting this
    /// drives the legacy <see cref="SelectedSourceEndpoint"/> binding,
    /// plus the snapshot-mode tracking properties below.
    /// </summary>
    [ObservableProperty] private BatchSourceItem? _selectedSource;

    /// <summary>Whether Batch reads source SQL live or from a stored snapshot.</summary>
    [ObservableProperty] private BatchSourceMode _sourceMode = BatchSourceMode.Live;

    /// <summary>When <see cref="SourceMode"/> is Snapshot, the id of the snapshot to replay.</summary>
    [ObservableProperty] private string? _sourceSnapshotId;

    /// <summary>Friendly name of the picked snapshot — drives the source badge ("(from snapshot X)").</summary>
    [ObservableProperty] private string? _sourceSnapshotDisplayName;

    /// <summary>Drives the "from snapshot…" badge visibility next to the source picker.</summary>
    public bool IsSnapshotSource => SourceMode == BatchSourceMode.Snapshot;

    /// <summary>Re-entrancy guard for the SelectedSource ↔ SelectedSourceEndpoint pingpong.</summary>
    private bool _syncingSourceItem;

    /// <summary>
    /// Live mirror of every <see cref="TargetPickVm"/> with IsChecked=true.
    /// The view's tag-chip strip binds to this so the user can see every
    /// selected target at a glance (mirroring how the source picker shows
    /// its current selection as a coloured badge). Kept in sync by
    /// <see cref="OnTargetPropertyChanged"/> and <see cref="RebuildTargets"/>.
    /// </summary>
    public ObservableCollection<TargetPickVm> CheckedTargets { get; } = new();

    /// <summary>
    /// First N of <see cref="CheckedTargets"/> — what the toolbar chip
    /// strip actually renders so it doesn't push past the row width when
    /// many targets are ticked. Excess is surfaced via
    /// <see cref="CheckedTargetsOverflow"/> + a "+N more" pill.
    /// </summary>
    public ObservableCollection<TargetPickVm> CheckedTargetsVisible { get; } = new();

    /// <summary>
    /// Tail beyond the visible cap — shown in the +N flyout when the user
    /// hovers / clicks the overflow pill. Identical content to the
    /// visible slice; just split for layout reasons.
    /// </summary>
    public ObservableCollection<TargetPickVm> CheckedTargetsOverflow { get; } = new();

    /// <summary>Max chips rendered inline in the toolbar before overflowing into +N.</summary>
    private const int VisibleTargetChipsMax = 3;

    /// <summary>Bound to the overflow pill — shows "+N" so the user knows there are more selections beyond the visible ones.</summary>
    public int CheckedTargetsOverflowCount => CheckedTargetsOverflow.Count;

    /// <summary>Drives the +N pill's IsVisible so it disappears when the count fits inline.</summary>
    public bool HasCheckedTargetsOverflow => CheckedTargetsOverflow.Count > 0;

    /// <summary>
    /// Pick proxy bound to the "Add target" AutoCompleteBox. Setting it
    /// ticks the matching <see cref="TargetPickVm"/> and then resets to
    /// null so the picker is ready for the next add. Pattern matches the
    /// way Source's <see cref="SelectedSourceEndpoint"/> drives its
    /// underlying state — but for many-at-a-time instead of one.
    /// </summary>
    [ObservableProperty] private EndpointPick? _nextTargetEndpoint;

    public bool CanSwap =>
        SelectedSourceEndpoint is not null && Targets.Count(t => t.IsChecked) == 1;

    /// <summary>
    /// Swap only makes sense for a 1-to-1 pairing. Once the user has picked
    /// multiple targets the operation is ambiguous, so the button hides
    /// entirely rather than sitting there disabled. Stays visible at 0 or 1
    /// ticked targets so the affordance is discoverable.
    /// </summary>
    public bool IsSwapVisible => Targets.Count(t => t.IsChecked) <= 1;

    public int TargetSelectedCount => Targets.Count(t => t.IsChecked);
    public int TargetTotalCount    => Targets.Count;
    public ObservableCollection<string>        StatusFilterOptions { get; } = new()
    {
        "All", "Pending", "Running", "Success", "Failed", "Skipped"
    };

    public BatchViewModel(AppServices svc)
    {
        _svc = svc;
        Reload();

        Items.CollectionChanged += OnItemsCollectionChanged;
        _ = RefreshDacpacAvailabilityAsync();
    }

    /// <summary>
    /// Preserves a legacy single-target surface used by MainWindow when
    /// Watch hands a list off to Batch. Setting it adds / selects the
    /// corresponding target in <see cref="Targets"/>.
    /// </summary>
    public string? TargetEnv
    {
        get => Targets.FirstOrDefault(t => t.IsChecked)?.Environment;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var db = SourceDatabase;
            if (string.IsNullOrWhiteSpace(db)) return;
            // Uncheck everything else so the assignment is intent-preserving.
            foreach (var t in Targets)
                t.IsChecked = string.Equals(t.Environment, value, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(t.Database,   db,    StringComparison.OrdinalIgnoreCase);
            OnPropertyChanged();
        }
    }

    /// <summary>Legacy alias — maps to <see cref="SourceDatabase"/>.</summary>
    public string? Database
    {
        get => SourceDatabase;
        set => SourceDatabase = value;
    }

    public async Task RefreshDacpacAvailabilityAsync()
    {
        var opts = await _svc.DacpacOptions.LoadAsync();
        DacpacConfigured = opts.IsUsable;
        if (!_dacpacDefaultsApplied)
        {
            _dacpacDefaultsApplied = true;
            StageAsDacpacBranch    = DacpacConfigured && opts.StageInGit;
        }
        if (!DacpacConfigured) StageAsDacpacBranch = false;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        // Subscribe to per-item Status changes so the filter view reacts live.
        if (e.NewItems is not null)
            foreach (BatchItem it in e.NewItems) it.PropertyChanged += OnItemPropertyChanged;
        if (e.OldItems is not null)
            foreach (BatchItem it in e.OldItems) it.PropertyChanged -= OnItemPropertyChanged;
        RebuildFilteredItems();
    }

    /// <summary>
    /// True while an execute / backup run is in progress. Used to suppress
    /// per-status-change filter rebuilds: without this, every time a row
    /// transitions to <see cref="BatchStatus.Running"/> the filtered list
    /// drops it (because the filter wants Skipped / Success / etc.), which
    /// makes rows visibly jump in and out mid-run.
    /// </summary>
    private bool _suppressFilterRebuild;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchItem.IsSelected))
        {
            RefreshSelectAllState();
            return;
        }
        if (e.PropertyName != nameof(BatchItem.Status)) return;
        if (_suppressFilterRebuild) return;
        RebuildFilteredItems();
    }

    /// <summary>
    /// Tri-state for the items-grid "select all" header affordance:
    /// <c>true</c> = every visible row ticked, <c>false</c> = none ticked,
    /// <c>null</c> = mixed. Drives <see cref="SelectAllGlyph"/>.
    /// </summary>
    [ObservableProperty] private bool? _allItemsChecked = false;

    /// <summary>
    /// Glyph painted inside the column-header "select all" Button.
    /// Same Button-styled-as-checkbox trick we use on the diff grid —
    /// Avalonia won't render a real CheckBox in a DataGrid column header.
    /// </summary>
    public string SelectAllGlyph => AllItemsChecked switch
    {
        true  => "✓",
        null  => "–",   // en-dash → "mixed"
        _     => "",
    };

    partial void OnAllItemsCheckedChanged(bool? value)
        => OnPropertyChanged(nameof(SelectAllGlyph));

    private void RefreshSelectAllState()
    {
        var total = FilteredItems.Count;
        bool? next;
        if (total == 0) next = false;
        else
        {
            var ticked = FilteredItems.Count(i => i.IsSelected);
            next = ticked == 0 ? false : ticked == total ? true : (bool?)null;
        }
        if (AllItemsChecked != next) AllItemsChecked = next;
    }

    /// <summary>
    /// Header click: ticks every visible row, or clears them if any
    /// are already ticked. "Visible" = <see cref="FilteredItems"/>,
    /// so the status / name filter scopes what gets touched.
    /// </summary>
    [RelayCommand]
    private void ToggleAllItems()
    {
        if (FilteredItems.Count == 0) return;
        var anySelected = FilteredItems.Any(i => i.IsSelected);
        foreach (var i in FilteredItems) i.IsSelected = !anySelected;
    }

    partial void OnStatusFilterChanged(string value) => RebuildFilteredItems();
    partial void OnNameFilterChanged(string value)   => RebuildFilteredItems();

    private void RebuildFilteredItems()
    {
        FilteredItems.Clear();
        BatchStatus? want = StatusFilter switch
        {
            "Pending"  => BatchStatus.Pending,
            "Running"  => BatchStatus.Running,
            "Success"  => BatchStatus.Success,
            "Failed"   => BatchStatus.Failed,
            "Skipped"  => BatchStatus.Skipped,
            _          => null
        };
        var nameNeedle = (NameFilter ?? "").Trim();
        foreach (var it in Items)
        {
            if (want is not null && it.Status != want) continue;
            if (nameNeedle.Length > 0 &&
                !it.Name.Contains(nameNeedle, StringComparison.OrdinalIgnoreCase))
                continue;
            FilteredItems.Add(it);
        }
        // Filter changed → visible set changed → header glyph may need to flip.
        RefreshSelectAllState();
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].Index = i + 1;
    }

    public void Reload()
    {
        Environments.Clear();
        foreach (var e in EnvironmentListProvider.Environments(_svc)) Environments.Add(e);
        Databases.Clear();
        foreach (var d in EnvironmentListProvider.Databases(_svc)) Databases.Add(d);

        Endpoints.Clear();
        foreach (var ep in EnvironmentListProvider.Endpoints(_svc)) Endpoints.Add(ep);

        // Reconcile the previously-picked source against the new visible
        // endpoint set. When the active connection group changes, the old
        // source may no longer be visible — without this the picker text
        // clears but SourceEnv/SourceDatabase persist, leaving the colour
        // badge stuck on the previous selection.
        var match = Endpoints.FirstOrDefault(e =>
            string.Equals(e.Environment, SourceEnv,      StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase));
        _suspendRebuild = true;
        try
        {
            if (match is null)
            {
                var firstEp = Endpoints.FirstOrDefault();
                SourceEnv      = firstEp?.Environment;
                SourceDatabase = firstEp?.Database;
            }
            else
            {
                // Snap to the canonical casing the catalog returned.
                SourceEnv      = match.Environment;
                SourceDatabase = match.Database;
            }
        }
        finally { _suspendRebuild = false; }

        RefreshProfiles();
        RebuildTargets();
        SyncSelectedEndpoint();
        ReloadProfiles();
    }

    private void ReloadProfiles()
    {
        var keepId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _svc.AppSettings.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Profiles.Add(p);
        if (!string.IsNullOrEmpty(keepId))
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == keepId);
    }

    partial void OnSourceEnvChanged(string? value)
    {
        SyncSelectedEndpoint();
        if (_suspendRebuild) return;
        RefreshProfiles();
        RebuildTargets();
    }
    partial void OnSourceDatabaseChanged(string? value)
    {
        SyncSelectedEndpoint();
        if (_suspendRebuild) return;
        RefreshProfiles();
        RebuildTargets();
    }

    partial void OnSelectedSourceEndpointChanged(EndpointPick? value)
    {
        // Reverse-direction sync with the unified source picker: when
        // legacy code (Watch handoff, Profile apply, Swap) sets the
        // endpoint, also reflect it as the matching Live row in
        // SourceCandidates so the picker visibly shows it. Skip if
        // we're already mid-sync from OnSelectedSourceChanged.
        if (!_syncingSourceItem && value is not null)
        {
            var liveMatch = SourceCandidates.FirstOrDefault(s => !s.IsSnapshot && s.Endpoint.Key == value.Key);
            if (liveMatch is not null && !ReferenceEquals(liveMatch, SelectedSource))
            {
                _syncingSourceItem = true;
                try { SelectedSource = liveMatch; }
                finally { _syncingSourceItem = false; }
                SourceMode = BatchSourceMode.Live;
                SourceSnapshotId = null;
                SourceSnapshotDisplayName = null;
                OnPropertyChanged(nameof(IsSnapshotSource));
            }
        }

        if (_syncingEndpoint || value is null) return;
        if (string.Equals(value.Environment, SourceEnv,      StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
            return;

        _syncingEndpoint = true;
        try
        {
            _suspendRebuild = true;
            try
            {
                SourceEnv      = value.Environment;
                SourceDatabase = value.Database;
            }
            finally { _suspendRebuild = false; }
        }
        finally { _syncingEndpoint = false; }

        RefreshProfiles();
        RebuildTargets();
    }

    partial void OnSelectedProfileChanged(EndpointProfile? value)
    {
        if (value is null) return;
        ApplyProfile(value);
    }

    private void SyncSelectedEndpoint()
    {
        if (_syncingEndpoint) return;
        var match = Endpoints.FirstOrDefault(e =>
            string.Equals(e.Environment, SourceEnv,      StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(match, SelectedSourceEndpoint)) return;
        _syncingEndpoint = true;
        try { SelectedSourceEndpoint = match; }
        finally { _syncingEndpoint = false; }
    }

    private void ApplyProfile(EndpointProfile p)
    {
        _suspendRebuild = true;
        try
        {
            SourceEnv      = p.SourceEnv;
            SourceDatabase = p.SourceDatabase;
        }
        finally { _suspendRebuild = false; }

        RefreshProfiles();
        RebuildTargets();

        var keys = new HashSet<string>(
            p.TargetKeys ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in Targets) t.IsChecked = keys.Contains(t.Key);
    }

    private void RefreshProfiles()
    {
        SourceProfile = (SourceEnv is null || SourceDatabase is null)
            ? null : _svc.Connections.GetProfile(SourceEnv, SourceDatabase);
    }

    private void RebuildTargets()
    {
        var previouslyChecked = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();

        // Detach IsChecked listeners before clearing so we don't leak.
        foreach (var t in Targets) t.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Clear();
        CheckedTargets.Clear();

        // Live source can't sync to itself (target would overwrite the
        // source mid-run), so the same-(env,db) row is omitted. Snapshot
        // source has no such concern — the snapshot is already a frozen
        // copy on disk — so the live same-DB IS a valid target (this is
        // the "restore from snapshot" workflow). Same branch as the
        // target-candidate filter in RebuildEndpointCandidatesCore, kept
        // here so the master Targets list AND the dropdown agree —
        // otherwise the user could pick the same-DB row in the dropdown
        // (it'd be present in TargetCandidateEndpoints) but nothing
        // would happen because OnNextTargetEndpointChanged couldn't find
        // a matching TargetPickVm in this Targets collection.
        var excludeSourceFromTargets = SourceMode == BatchSourceMode.Live;

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            if (excludeSourceFromTargets
                && string.Equals(cfg.Environment, SourceEnv, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cfg.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
                continue;

            var keyCheck = $"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}";
            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains(keyCheck));
            pick.PropertyChanged += OnTargetPropertyChanged;
            Targets.Add(pick);
            if (pick.IsChecked) CheckedTargets.Add(pick);
        }
        RebuildFilteredTargets();
        RebuildEndpointCandidates();
        RebuildCheckedTargetSlices();
        NotifyTargetCounts();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TargetPickVm.IsChecked)) return;
        NotifyTargetCounts();

        // Mirror IsChecked transitions into CheckedTargets so the view's
        // tag strip stays in sync. Using add/remove (not Clear+rebuild)
        // keeps the chip animations / focus stable when an item flips.
        if (sender is TargetPickVm vm)
        {
            if (vm.IsChecked && !CheckedTargets.Contains(vm))
                CheckedTargets.Add(vm);
            else if (!vm.IsChecked)
                CheckedTargets.Remove(vm);
        }

        // A ticked target should disappear from BOTH dropdowns; an
        // unticked one should reappear in the target picker.
        RebuildEndpointCandidates();
        RebuildCheckedTargetSlices();
    }

    /// <summary>
    /// Re-partition <see cref="CheckedTargets"/> into the visible /
    /// overflow slices. Visible holds the first <see cref="VisibleTargetChipsMax"/>
    /// items; the rest go into overflow and surface via a "+N more" pill.
    /// Called whenever the checked set changes.
    /// </summary>
    private void RebuildCheckedTargetSlices()
    {
        CheckedTargetsVisible.Clear();
        CheckedTargetsOverflow.Clear();
        var i = 0;
        foreach (var t in CheckedTargets)
        {
            if (i < VisibleTargetChipsMax) CheckedTargetsVisible.Add(t);
            else                           CheckedTargetsOverflow.Add(t);
            i++;
        }
        OnPropertyChanged(nameof(CheckedTargetsOverflowCount));
        OnPropertyChanged(nameof(HasCheckedTargetsOverflow));
    }

    /// <summary>
    /// Recompute the source / target candidate lists used by their
    /// respective AutoCompleteBoxes. Excluded sets:
    ///   SourceCandidateEndpoints = Endpoints \ ticked targets
    ///   TargetCandidateEndpoints = Endpoints \ source \ ticked targets
    /// Triggered whenever Endpoints / source / ticked targets change.
    ///
    /// IMPORTANT: the actual mutation is deferred to the next dispatcher
    /// tick via <see cref="Avalonia.Threading.Dispatcher.UIThread.Post"/>.
    /// Without this, picking an item in either AutoCompleteBox would
    /// synchronously clear + rebuild the very <c>ItemsSource</c> the
    /// control is mid-processing and crash. Posting back to the UI
    /// thread lets the current binding callback finish first.
    /// </summary>
    private void RebuildEndpointCandidates()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            RebuildEndpointCandidatesCore,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RebuildEndpointCandidatesCore()
    {
        bool MatchesSource(EndpointPick ep) =>
            !string.IsNullOrWhiteSpace(SourceEnv) && !string.IsNullOrWhiteSpace(SourceDatabase) &&
            string.Equals(ep.Environment, SourceEnv,      StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ep.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase);

        bool MatchesAnyTicked(EndpointPick ep) =>
            CheckedTargets.Any(t =>
                string.Equals(t.Environment, ep.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Database,    ep.Database,    StringComparison.OrdinalIgnoreCase));

        // When source is LIVE, the source DB can't sync to itself (target
        // would overwrite the source mid-run) — so the live source is
        // excluded from the target list. When source is a SNAPSHOT, the
        // same database is a legitimate target: applying an old snapshot
        // back to its own live DB is the "rollback / restore" use case
        // that was previously impossible.
        var excludeSourceFromTargets = SourceMode == BatchSourceMode.Live;

        SourceCandidateEndpoints.Clear();
        TargetCandidateEndpoints.Clear();
        foreach (var ep in Endpoints)
        {
            if (!MatchesAnyTicked(ep))    SourceCandidateEndpoints.Add(ep);
            if (!MatchesAnyTicked(ep) && !(excludeSourceFromTargets && MatchesSource(ep)))
                                          TargetCandidateEndpoints.Add(ep);
        }

        RebuildSourceCandidates(MatchesAnyTicked);
    }

    /// <summary>
    /// SourceMode flipping between Live and Snapshot changes whether the
    /// source DB is a valid target. We have to refresh BOTH the master
    /// Targets list AND the candidate dropdown:
    ///   • RebuildTargets() — appends or skips the same-(env,db) row in
    ///     the master TargetPickVm collection. Without this, picking the
    ///     row in the dropdown does nothing because OnNextTargetEndpoint
    ///     looks it up by (env,db) and never finds a match.
    ///   • RebuildEndpointCandidates() — refreshes the dropdown so the
    ///     same-DB row appears (or disappears) immediately.
    /// SourceMode flips *after* SelectedSourceEndpoint changes (see
    /// OnSelectedSourceChanged), so by the time we get here SourceEnv /
    /// SourceDatabase already point at the new endpoint.
    /// </summary>
    partial void OnSourceModeChanged(BatchSourceMode value)
    {
        RebuildTargets();
        RebuildEndpointCandidates();
    }

    /// <summary>
    /// Rebuild the unified source picker list: one entry per live
    /// endpoint (skipping any that are currently ticked as a target),
    /// plus one entry per snapshot of every endpoint's local store.
    /// Snapshots of a ticked-target endpoint stay in the list — that's
    /// the legitimate "rollback to an old snapshot of the same DB"
    /// use case, which would otherwise be invisible.
    /// </summary>
    private void RebuildSourceCandidates(Func<EndpointPick, bool> matchesTicked)
    {
        // Preserve whichever item the user had selected so re-rendering
        // the list doesn't yank the picker back to default.
        var keepKey = SelectedSource?.Key;

        SourceCandidates.Clear();
        foreach (var ep in Endpoints)
        {
            if (!matchesTicked(ep))
                SourceCandidates.Add(new BatchSourceItem(ep, Snapshot: null));

            // Pull snapshots from the local store for this endpoint.
            // Stores are lazy — opening one for an endpoint with no
            // snapshots is cheap (just a Directory.Exists check).
            try
            {
                var store = _svc.OpenSchemaStore(ep.Environment, ep.Database);
                foreach (var snap in store.ListSnapshots())
                    SourceCandidates.Add(new BatchSourceItem(ep, snap));
            }
            catch { /* store unreadable — skip, source picker should never blow up */ }
        }

        if (!string.IsNullOrEmpty(keepKey))
        {
            var match = SourceCandidates.FirstOrDefault(s => s.Key == keepKey);
            if (match is not null && !ReferenceEquals(match, SelectedSource))
            {
                _syncingSourceItem = true;
                try { SelectedSource = match; }
                finally { _syncingSourceItem = false; }
            }
        }
    }

    /// <summary>
    /// User picked a row in the source dropdown — sync legacy
    /// <see cref="SelectedSourceEndpoint"/> and capture snapshot info
    /// when applicable. The <see cref="_syncingSourceItem"/> flag
    /// breaks the loop between this handler and the reverse handler in
    /// <see cref="OnSelectedSourceEndpointChanged"/>.
    /// </summary>
    partial void OnSelectedSourceChanged(BatchSourceItem? value)
    {
        if (_syncingSourceItem) return;
        if (value is null)
        {
            SourceMode = BatchSourceMode.Live;
            SourceSnapshotId = null;
            SourceSnapshotDisplayName = null;
            OnPropertyChanged(nameof(IsSnapshotSource));
            return;
        }

        _syncingSourceItem = true;
        try
        {
            // Update legacy endpoint binding — fires the existing
            // OnSelectedSourceEndpointChanged which updates
            // SourceEnv / SourceDatabase / RebuildTargets etc.
            SelectedSourceEndpoint = value.Endpoint;
        }
        finally { _syncingSourceItem = false; }

        SourceMode = value.IsSnapshot ? BatchSourceMode.Snapshot : BatchSourceMode.Live;
        SourceSnapshotId = value.Snapshot?.Id;
        SourceSnapshotDisplayName = value.Snapshot?.DisplayName;
        OnPropertyChanged(nameof(IsSnapshotSource));
    }

    /// <summary>
    /// Adding a target via the "Add target" picker. Setting
    /// <see cref="NextTargetEndpoint"/> ticks the matching target and
    /// resets the picker back to null so it's ready for the next add.
    /// Match is case-insensitive on (env, database).
    /// </summary>
    partial void OnNextTargetEndpointChanged(EndpointPick? value)
    {
        if (value is null) return;
        var t = Targets.FirstOrDefault(t =>
            string.Equals(t.Environment, value.Environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Database,    value.Database,    StringComparison.OrdinalIgnoreCase));
        if (t is not null && !t.IsChecked) t.IsChecked = true;
        // Reset so the picker shows the watermark again — without this the
        // user has to manually clear before adding a second target.
        NextTargetEndpoint = null;
    }

    /// <summary>
    /// Remove a single target from the selected set. Wired from the × on
    /// each chip in the view; equivalent to unticking the underlying
    /// <see cref="TargetPickVm"/>, which fans out through
    /// <see cref="OnTargetPropertyChanged"/> to remove from
    /// <see cref="CheckedTargets"/> automatically.
    /// </summary>
    public void UncheckTarget(TargetPickVm t)
    {
        if (t is null) return;
        t.IsChecked = false;
    }

    partial void OnTargetFilterChanged(string value) => RebuildFilteredTargets();

    private void RebuildFilteredTargets()
    {
        FilteredTargets.Clear();
        var f = (TargetFilter ?? "").Trim();
        foreach (var t in Targets)
            if (string.IsNullOrEmpty(f) || TargetMatches(t, f))
                FilteredTargets.Add(t);
    }

    private static bool TargetMatches(TargetPickVm t, string filter) =>
        t.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || t.Environment.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || t.Database.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void NotifyTargetCounts()
    {
        OnPropertyChanged(nameof(CanSwap));
        OnPropertyChanged(nameof(IsSwapVisible));
        OnPropertyChanged(nameof(TargetSelectedCount));
        OnPropertyChanged(nameof(TargetTotalCount));
    }

    [RelayCommand]
    private void SelectAllVisibleTargets()
    {
        foreach (var t in FilteredTargets) t.IsChecked = true;
    }

    [RelayCommand]
    private void ClearTargets()
    {
        foreach (var t in Targets) t.IsChecked = false;
    }

    [RelayCommand]
    private async Task SaveAsProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase))
        {
            _svc.Toasts.Warning("Pick a source first", "Pick a source endpoint before saving a profile.");
            return;
        }

        var name = await PromptDialog.AskAsync(
            title:        "Save profile",
            message:      "Name this source/target combination so you can pick it again with one click.",
            initialValue: SuggestProfileName(),
            watermark:    "e.g. Portal: DEV → PROD",
            primaryText:  "Save");
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = _svc.AppSettings.Profiles
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var ok = await ConfirmDialog.AskAsync(
                "Replace profile?",
                $"A profile named '{name}' already exists. Overwrite it?",
                primaryText: "Replace");
            if (!ok) return;
        }

        var profile = new EndpointProfile
        {
            Id             = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name           = name!,
            SourceEnv      = SourceEnv!,
            SourceDatabase = SourceDatabase!,
            TargetKeys     = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToList(),
        };
        _svc.AppSettings.UpsertProfile(profile);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        _svc.Toasts.Success("Profile saved", $"'{name}' is now in your profile list.");
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        var p = SelectedProfile;
        if (p is null) return;
        var ok = await ConfirmDialog.AskAsync(
            "Delete profile?",
            $"Delete the profile '{p.Name}'? This won't affect your connections or saved data.");
        if (!ok) return;
        _svc.AppSettings.RemoveProfile(p.Id);
        SelectedProfile = null;
        ReloadProfiles();
        _svc.Toasts.Info("Profile deleted", $"'{p.Name}' was removed.");
    }

    [RelayCommand]
    private void Swap()
    {
        if (!CanSwap) return;
        var t = Targets.First(x => x.IsChecked);
        var oldEnv = SourceEnv;
        var oldDb  = SourceDatabase;

        _suspendRebuild = true;
        try
        {
            SourceEnv      = t.Environment;
            SourceDatabase = t.Database;
        }
        finally { _suspendRebuild = false; }
        RefreshProfiles();
        RebuildTargets();

        var newKey = $"{oldEnv?.ToUpperInvariant()}|{oldDb?.ToUpperInvariant()}";
        foreach (var x in Targets) x.IsChecked = x.Key == newKey;
        SyncSelectedEndpoint();
    }

    private string SuggestProfileName()
    {
        var first = Targets.FirstOrDefault(t => t.IsChecked);
        return first is null
            ? $"{SourceEnv}/{SourceDatabase}"
            : $"{SourceDatabase}: {SourceEnv} → {first.Environment}";
    }

    /// <summary>
    /// Decide the run-stamp the next backup / execute click will use:
    ///   - When <see cref="UseAutoBackupName"/> is true → fresh millisecond
    ///     timestamp, no prompt.
    ///   - When false → pop a name prompt; validate uniqueness against
    ///     today's date folder; loop until the user enters a free name or
    ///     cancels. Cancel returns false so the caller bails cleanly.
    /// Returns <c>("", false)</c> on cancel, <c>(stamp, true)</c> on success.
    /// </summary>
    private async Task<(string Stamp, bool Ok)> ResolveRunStampAsync(string promptTitle)
    {
        CustomBackupNameError = "";

        if (UseAutoBackupName)
            return (Base.It.Core.Backup.FileBackupStore.NewRunStamp(), true);

        // Loop until valid + free, or the user cancels. Showing the dialog
        // multiple times rather than reporting an error from the toolbar
        // keeps the conflict resolution close to where the user just typed.
        string suggested = (CustomBackupName ?? "").Trim();
        while (true)
        {
            var input = await PromptDialog.AskAsync(
                title:        promptTitle,
                message:      "Name this run's backup folder so you can find it later (e.g. 'before-feature-x'). Must be unique within today's backups.",
                initialValue: suggested,
                watermark:    "Backup name",
                primaryText:  "OK");
            if (string.IsNullOrWhiteSpace(input)) return ("", false);

            input = input.Trim();
            if (_svc.Backups.IsRunStampInUseToday(input))
            {
                _svc.Toasts.Warning("Name in use", $"A backup folder named '{input}' already exists for today.");
                suggested = input;
                continue;
            }

            CustomBackupName = input;
            return (input, true);
        }
    }

    /// <summary>
    /// Loads object names from either a local CSV/XLSX path OR an
    /// HTTP(S) URL (e.g. a Google Sheets CSV export). Both routes share
    /// the same parsing rule — skip the first row, take the first
    /// column — so a sheet that works as a download also works as a
    /// pasted link.
    ///
    /// <para>Sets <see cref="IsBusy"/> + <see cref="Status"/> while
    /// fetching so the user sees the load actually progressing (URL
    /// fetches can take a second or two with no other feedback).</para>
    /// </summary>
    [RelayCommand]
    private async Task LoadFromFileAsync()
    {
        var source = (FilePath ?? "").Trim();
        if (string.IsNullOrEmpty(source))
        {
            Status = "Pick a CSV / XLSX or paste a URL.";
            _svc.Toasts.Warning("Pick a file or URL", "Provide a .csv / .xlsx path, or a URL.");
            return;
        }

        var isUrl = source.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)
                 || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isUrl && !File.Exists(source))
        {
            Status = "File not found.";
            _svc.Toasts.Warning("Not found", source);
            return;
        }

        var label = isUrl
            ? (Uri.TryCreate(source, UriKind.Absolute, out var u) ? u.Host : "url")
            : Path.GetFileName(source);

        IsBusy = true;
        Status = isUrl ? $"Fetching from {label}…" : $"Loading {label}…";

        try
        {
            IReadOnlyList<string> names = isUrl
                ? await ObjectListLoader.FromUrlAsync(source)
                : ObjectListLoader.FromFile(source);

            Items.Clear();
            foreach (var n in names) Items.Add(new BatchItem(n));

            if (Items.Count == 0)
            {
                Status = $"No object names found in {label} (first row is treated as a header; only column 1 is used).";
                _svc.Toasts.Warning("Empty list", $"No usable rows in {label}.");
            }
            else
            {
                Status = $"Loaded {Items.Count} object(s) from {label}.";
                _svc.Toasts.Success("List loaded", $"{Items.Count} object(s) from {label}.");
            }
        }
        catch (Exception ex)
        {
            // ObjectListLoader throws InvalidOperationException for HTML-instead-of-sheet —
            // surface its message verbatim, it already explains the fix.
            Status = $"Load failed: {ex.Message}";
            _svc.Toasts.Error("Load failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveSelected(BatchItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
    }

    /// <summary>
    /// Builds a <see cref="BatchPreviewViewModel"/> for the given row,
    /// combining the current source with every ticked target. Used by
    /// the row's eye icon to open a side-by-side SQL preview before the
    /// user clicks Execute. Returns null when the source isn't set —
    /// nothing to preview against. Connection strings are resolved once
    /// here so the preview window can work even after the source/target
    /// selection changes underneath it.
    /// </summary>
    public async Task<BatchPreviewViewModel?> BuildPreviewAsync(BatchItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Name)) return null;
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase))
            return null;

        // Targets are always live endpoints (sync writes to live DBs),
        // so they're fetched the same way in either source mode.
        var targetEndpoints = new List<PreviewEndpoint>();
        foreach (var t in Targets.Where(t => t.IsChecked))
        {
            var tgtConn  = _svc.Connections.Get(t.Environment, t.Database) ?? "";
            var profile  = _svc.Connections.GetProfile(t.Environment, t.Database);
            targetEndpoints.Add(new PreviewEndpoint(
                Label:            $"Target · {t.Environment} / {t.Database}",
                Color:            profile?.Color,
                ConnectionString: tgtConn));
        }

        // Snapshot source → source pane is the literal SQL stored in the
        // snapshot, NOT a live fetch. Without this, the preview would try
        // to hit the source endpoint (which may not even have the object
        // any more) and surface a "not found in the endpoint" error,
        // defeating the whole point of snapshot-as-source.
        if (SourceMode == BatchSourceMode.Snapshot
            && !string.IsNullOrWhiteSpace(SourceSnapshotId))
        {
            var store = _svc.OpenSchemaStore(SourceEnv!, SourceDatabase!);
            var snap  = await store.ReadSnapshotAsync(SourceSnapshotId);
            if (snap is not null)
            {
                var entry = snap.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, item.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    var sourceSql = await store.ReadObjectAsync(entry.Hash);
                    if (!string.IsNullOrWhiteSpace(sourceSql))
                    {
                        var snapLabel = string.IsNullOrWhiteSpace(SourceSnapshotDisplayName)
                            ? "snapshot"
                            : SourceSnapshotDisplayName;
                        return BatchPreviewViewModel.ForFileAndTargets(
                            svc:         _svc,
                            sourceLabel: $"Source · {SourceEnv} / {SourceDatabase} @ {snapLabel}",
                            fileContent: sourceSql!,
                            objectName:  item.Name.Trim(),
                            targets:     targetEndpoints);
                    }
                }
            }
            // Snapshot mode but the object isn't in this snapshot — fall
            // through so the user at least sees the targets, with a
            // "not in snapshot" placeholder on the source side.
            return BatchPreviewViewModel.ForFileAndTargets(
                svc:         _svc,
                sourceLabel: $"Source · {SourceEnv} / {SourceDatabase} (snapshot)",
                fileContent: $"-- '{item.Name}' is not present in the selected snapshot.",
                objectName:  item.Name.Trim(),
                targets:     targetEndpoints);
        }

        // Live source path (original behaviour).
        var srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!) ?? "";
        var endpoints = new List<PreviewEndpoint>
        {
            new PreviewEndpoint(
                Label:            $"Source · {SourceEnv} / {SourceDatabase}",
                Color:            SourceProfile?.Color,
                ConnectionString: srcConn)
        };
        endpoints.AddRange(targetEndpoints);
        return new BatchPreviewViewModel(_svc, item.Name.Trim(), endpoints);
    }

    /// <summary>
    /// Surface a toast after the view copies a batch of object names to
    /// the clipboard. View owns the clipboard call (TopLevel access lives
    /// in the visual tree); VM just routes the count into the shared
    /// toast service so the user gets the same confirmation shape Batch
    /// uses elsewhere ("Pasted from clipboard", "Rows removed", …).
    /// </summary>
    public void NotifyCopied(int count)
    {
        if (count <= 0) return;
        _svc.Toasts.Info(
            "Copied to clipboard",
            count == 1 ? "1 object name copied." : $"{count} object names copied.");
    }

    /// <summary>
    /// Append items from clipboard / external paste. Splits on CR/LF,
    /// trims each line, drops blanks, and skips entries that are
    /// already in <see cref="Items"/> (case-insensitive on Name) so a
    /// re-paste doesn't double the list. Returns the count actually
    /// added so the caller can surface a toast — nothing else changes
    /// when nothing was added (e.g. clipboard had blank text).
    /// </summary>
    public int PasteText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var existing = new HashSet<string>(
            Items.Select(i => i.Name),
            StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var raw in lines)
        {
            var name = raw.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!existing.Add(name)) continue;
            Items.Add(new BatchItem(name));
            added++;
        }
        if (added > 0)
        {
            Status = $"Pasted {added} object(s). Total: {Items.Count}.";
            _svc.Toasts.Success("Pasted from clipboard", $"{added} added · {Items.Count} total.");
        }
        else
        {
            _svc.Toasts.Info("Nothing pasted", "Clipboard didn't contain any new object names.");
        }
        return added;
    }

    /// <summary>
    /// Remove the given rows from the list. Used by the DataGrid's
    /// Delete-key handler in the view; also useful for any future
    /// bulk-remove command. Caller passes a snapshot — we don't
    /// re-enumerate the live SelectedItems here because that
    /// collection mutates as we remove.
    /// </summary>
    public int DeleteRows(IEnumerable<BatchItem> rows)
    {
        var snapshot = rows?.ToList() ?? new List<BatchItem>();
        if (snapshot.Count == 0) return 0;
        foreach (var r in snapshot) Items.Remove(r);
        Status = $"Removed {snapshot.Count} row(s). {Items.Count} remaining.";
        _svc.Toasts.Info("Rows removed", $"Removed {snapshot.Count} · {Items.Count} remaining.");
        return snapshot.Count;
    }

    /// <summary>
    /// Remove every row whose checkbox is ticked. Useful for cleaning up
    /// after a partial batch run without having to clear everything.
    /// </summary>
    [RelayCommand]
    private void RemoveChecked()
    {
        var checkedItems = Items.Where(i => i.IsSelected).ToList();
        if (checkedItems.Count == 0)
        {
            Status = "No rows ticked — use the checkboxes to pick rows to remove.";
            _svc.Toasts.Warning("No rows selected", "Tick one or more rows before clicking 'Remove selected'.");
            return;
        }
        foreach (var it in checkedItems) Items.Remove(it);
        Status = $"Removed {checkedItems.Count} row(s). {Items.Count} remaining.";
        _svc.Toasts.Info("Rows removed", $"Removed {checkedItems.Count} · {Items.Count} remaining.");
    }

    [RelayCommand]
    private void Clear()
    {
        if (Items.Count == 0)
        {
            _svc.Toasts.Info("Nothing to clear", "The batch is already empty.");
            return;
        }
        var n = Items.Count;
        Items.Clear();
        SuccessCount = FailCount = 0;
        Status = "Cleared.";
        _svc.Toasts.Info("Batch cleared", $"Removed {n} row(s).");
    }

    /// <summary>
    /// Default Execute respects the active filter / search — runs every row
    /// in <see cref="FilteredItems"/>. Hidden rows aren't touched. Pattern
    /// matches every other "table action" in the app (Search → see filtered
    /// → act on what you see).
    /// </summary>
    [RelayCommand]
    private Task ExecuteAsync() => ExecuteCoreAsync(FilteredItems.ToList(), "Nothing visible to run", "filtered rows");

    /// <summary>
    /// Execute Selected runs only rows where the row's checkbox is ticked
    /// — same convention as Remove Selected. Lets the user pin a subset
    /// with the row checkboxes and run just that.
    /// </summary>
    [RelayCommand]
    private Task ExecuteSelectedAsync() => ExecuteCoreAsync(
        Items.Where(i => i.IsSelected).ToList(),
        emptyMsg: "Tick rows first",
        scopeLabel: "selected rows");

    private async Task ExecuteCoreAsync(List<BatchItem> work, string emptyMsg, string scopeLabel)
    {
        if (work.Count == 0)
        {
            Status = $"No {scopeLabel} to execute.";
            _svc.Toasts.Warning(emptyMsg, $"Nothing in {scopeLabel} to run.");
            return;
        }

        // Major-action gate: Execute mutates every ticked target. Confirm
        // before running so a stray click doesn't push 30 procs to PROD.
        // Spelled out in the message: rows × targets so the user sees the
        // real blast radius before saying yes.
        var targetCount = Targets.Count(t => t.IsChecked);
        var rowsLabel    = work.Count   == 1 ? "row"    : "rows";
        var targetsLabel = targetCount  == 1 ? "target" : "targets";
        var scopeLine    = scopeLabel.StartsWith("filtered")
            ? $"This will run {work.Count} filtered {rowsLabel} against {targetCount} {targetsLabel}."
            : $"This will run {work.Count} selected {rowsLabel} against {targetCount} {targetsLabel}.";
        var ok = await ConfirmDialog.AskAsync(
            title:       "Execute on targets?",
            message:     $"{scopeLine} Each existing object will be ALTERED; missing objects will be CREATED. Continue?",
            primaryText: "Execute",
            cancelText:  "Cancel");
        if (!ok) { Status = "Execute cancelled."; return; }
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase))
        {
            Status = "Pick source environment and database.";
            _svc.Toasts.Warning("Missing source", "Pick a source environment and database first.");
            return;
        }

        var checkedTargets = Targets.Where(t => t.IsChecked).ToList();
        if (checkedTargets.Count == 0)
        {
            Status = "Pick at least one target.";
            _svc.Toasts.Warning("No targets", "Tick one or more target connections.");
            return;
        }

        // Two source modes: Live reads from sourceConn at run time;
        // Snapshot reads pre-captured SQL from the local schema store.
        // Snapshot mode skips the live-connection check + DACPAC export
        // (the snapshot already IS a stored snapshot).
        string?                              srcConn          = null;
        Base.It.Core.Schema.SchemaStore?     snapshotStore    = null;
        Dictionary<string, Base.It.Core.Schema.SnapshotEntry>? snapshotEntries = null;
        string                               snapshotLabel    = "";

        if (SourceMode == BatchSourceMode.Snapshot)
        {
            if (string.IsNullOrWhiteSpace(SourceSnapshotId))
            {
                Status = "Pick a snapshot in the source dropdown.";
                _svc.Toasts.Warning("Snapshot source missing", "Pick a snapshot in the source dropdown.");
                return;
            }
            snapshotStore = _svc.OpenSchemaStore(SourceEnv!, SourceDatabase!);
            var snap = await snapshotStore.ReadSnapshotAsync(SourceSnapshotId);
            if (snap is null)
            {
                Status = "Snapshot not found in store.";
                _svc.Toasts.Error("Snapshot missing", $"Snapshot {SourceSnapshotId} isn't in the local store.");
                return;
            }
            // Index by FullName (case-insensitive) so item lookups are O(1).
            snapshotEntries = new Dictionary<string, Base.It.Core.Schema.SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in snap.Entries) snapshotEntries[e.FullName] = e;
            snapshotLabel = $"{SourceEnv}/{SourceDatabase} @ {SourceSnapshotDisplayName}";
        }
        else
        {
            srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!);
            if (string.IsNullOrWhiteSpace(srcConn))
            {
                Status = "Missing source connection string.";
                _svc.Toasts.Error("No source connection", $"{SourceEnv}·{SourceDatabase} isn't configured.");
                return;
            }
        }

        var exporter      = SourceMode == BatchSourceMode.Snapshot
            ? null                                       // DACPAC export doesn't apply when source is already stored.
            : await _svc.TryBuildDacpacExporterAsync();
        var exportedPaths = new List<string>();

        // Collect every backup file written during the batch so we can
        // produce a single consolidated zip at the end instead of one
        // tiny zip per object × target.
        var batchBackupPaths = new List<string>();

        // Resolve & validate the run-stamp BEFORE flipping IsBusy so the
        // error path doesn't leave the UI stuck on a spinner. When the
        // auto-name toggle is off, this awaits a popup for the user's
        // label and validates uniqueness against today's folder.
        var (batchRunStamp, stampOk) = await ResolveRunStampAsync("Name this execute run");
        if (!stampOk) return;
        IsBusy = true; SuccessCount = FailCount = 0;
        // Reset every row in the work-set BEFORE the loop so a re-run
        // doesn't mix stale Running / Success states with the new run's
        // progression. The filter rebuild is suppressed below, so the
        // batch status flicker is invisible to the user.
        _suppressFilterRebuild = true;
        foreach (var item in work)
        {
            item.Status  = BatchStatus.Pending;
            item.Message = "";
        }
        // ONE stamp for the whole batch click — every source + target
        // backup file lands under the same {date}\{stamp}_*\... tree so
        // a Scripts-pane revert can target one folder and run cleanly.
        try
        {
            foreach (var item in work)
            {
                item.Status  = BatchStatus.Running;
                item.Message = "";
                var perTargetMsgs = new List<string>();
                int itemOk = 0, itemFail = 0, itemNotFound = 0;

                try
                {
                    var id = ObjectIdentifier.Parse(item.Name.Trim());

                    // ── Snapshot source branch ────────────────────────────
                    if (SourceMode == BatchSourceMode.Snapshot)
                    {
                        // Look up the object in the snapshot. Match on the
                        // raw "schema.name" key produced by ObjectIdentifier
                        // — the snapshot index uses the same form.
                        var fullName = $"{id.Schema}.{id.Name}";
                        if (!snapshotEntries!.TryGetValue(fullName, out var entry))
                        {
                            item.Status  = BatchStatus.Failed;
                            item.Message = $"Not in snapshot ({SourceSnapshotDisplayName})";
                            FailCount++;
                            continue;
                        }
                        var sql = await snapshotStore!.ReadObjectAsync(entry.Hash);
                        if (sql is null)
                        {
                            item.Status  = BatchStatus.Failed;
                            item.Message = $"Snapshot object missing on disk (hash {entry.Hash[..12]}…)";
                            FailCount++;
                            continue;
                        }
                        var preFetched = new Base.It.Core.Models.SqlObject(
                            new ObjectIdentifier(entry.Schema, entry.Name),
                            entry.Kind, sql, entry.Hash);

                        int snapOk = 0, snapFail = 0;
                        foreach (var t in checkedTargets)
                        {
                            var tgtConn = _svc.Connections.Get(t.Environment, t.Database);
                            if (string.IsNullOrWhiteSpace(tgtConn))
                            {
                                perTargetMsgs.Add($"[{t.Environment}·{t.Database}] no connection");
                                snapFail++;
                                continue;
                            }
                            try
                            {
                                var r = await _svc.Sync.SyncFromDefinitionAsync(
                                    tgtConn!, preFetched, snapshotLabel, t.Environment,
                                    runStamp: batchRunStamp);
                                if (r.TargetBackupPath is not null) batchBackupPaths.Add(r.TargetBackupPath);
                                if (r.Status == Base.It.Core.Sync.SyncStatus.Success)
                                {
                                    perTargetMsgs.Add($"[{t.Environment}·{t.Database}] ok");
                                    snapOk++;
                                }
                                else
                                {
                                    perTargetMsgs.Add($"[{t.Environment}·{t.Database}] {r.Message}");
                                    snapFail++;
                                }
                            }
                            catch (Exception ex)
                            {
                                perTargetMsgs.Add($"[{t.Environment}·{t.Database}] error: {ex.Message}");
                                snapFail++;
                            }
                        }

                        item.Message = string.Join("  |  ", perTargetMsgs);
                        if (snapFail > 0)   { item.Status = BatchStatus.Failed;  FailCount++; }
                        else if (snapOk > 0){ item.Status = BatchStatus.Success; SuccessCount++; }
                        else                { item.Status = BatchStatus.Skipped; }
                        continue;
                    }

                    // ── Live source branch (original behaviour) ───────────
                    // Capture this row's source backup ONCE before the
                    // target loop, into the batch's run-folder. Without
                    // this, every target call would re-write the same
                    // source content under the source-env folder.
                    string? rowSourceBackup = null;
                    try
                    {
                        var srcOutcome = await _svc.Backup.BackupAsync(
                            srcConn!, SourceEnv!, id,
                            role: Base.It.Core.Backup.BackupRole.Source,
                            runStamp: batchRunStamp);
                        if (srcOutcome.Kind == Base.It.Core.Backup.BackupOutcomeKind.Written)
                        {
                            rowSourceBackup = srcOutcome.FilePath;
                            if (rowSourceBackup is not null) batchBackupPaths.Add(rowSourceBackup);
                        }
                    }
                    catch { /* best-effort — sync still runs even if pre-capture failed */ }

                    foreach (var t in checkedTargets)
                    {
                        var tgtConn = _svc.Connections.Get(t.Environment, t.Database);
                        if (string.IsNullOrWhiteSpace(tgtConn))
                        {
                            perTargetMsgs.Add($"[{t.Environment}·{t.Database}] no connection"); itemFail++;
                            continue;
                        }
                        try
                        {
                            // zipPair: false — Batch produces one consolidated
                            // zip after every item finishes, so per-target
                            // pair zips would be duplicative noise.
                            // captureSourceBackup: false — we already wrote
                            // the source-side backup once above.
                            var r = await _svc.Sync.SyncAsync(
                                srcConn!, tgtConn!, id, SourceEnv!, t.Environment,
                                ct: default, zipPair: false,
                                captureSourceBackup: false,
                                runStamp: batchRunStamp);
                            if (r.TargetBackupPath is not null) batchBackupPaths.Add(r.TargetBackupPath);
                            switch (r.Status)
                            {
                                case SyncStatus.Success:
                                    perTargetMsgs.Add($"[{t.Environment}·{t.Database}] ok");
                                    itemOk++;
                                    break;
                                case SyncStatus.NotFound:
                                    perTargetMsgs.Add($"[{t.Environment}·{t.Database}] not found");
                                    itemNotFound++;
                                    break;
                                default:
                                    perTargetMsgs.Add($"[{t.Environment}·{t.Database}] {r.Message}");
                                    itemFail++;
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            perTargetMsgs.Add($"[{t.Environment}·{t.Database}] error: {ex.Message}"); itemFail++;
                        }
                    }

                    // Aggregate row status: any failure → Failed; all not-found → Skipped; else Success.
                    if (itemFail > 0)       { item.Status = BatchStatus.Failed;  FailCount++; }
                    else if (itemOk > 0)    { item.Status = BatchStatus.Success; SuccessCount++; }
                    else if (itemNotFound > 0) item.Status = BatchStatus.Skipped;
                    else                       item.Status = BatchStatus.Skipped;

                    // DACPAC export is gated on the per-run checkbox. Unchecked
                    // = don't touch the DACPAC folder at all (no file writes,
                    // no git staging). Checked = write the DACPAC file and
                    // let the post-batch git step stage them on a branch or
                    // the current HEAD per user preference.
                    if (exporter is not null && itemOk > 0 && StageAsDacpacBranch)
                    {
                        try
                        {
                            // Routed through AppServices so the trigger-
                            // inline policy stays in one place. The result
                            // tuple's ExistedBefore flag preserves the
                            // "updated" vs "created (new)" log distinction
                            // even when a trigger ends up writing to its
                            // parent table's file.
                            var result = await _svc.ExportToDacpacAsync(exporter, srcConn!, id);
                            if (result.Path is not null)
                            {
                                exportedPaths.Add(result.Path);
                                var rel = Path.GetRelativePath(exporter.Options.RootFolder, result.Path);
                                perTargetMsgs.Add(result.ExistedBefore
                                    ? $"[DACPAC] updated {rel}"
                                    : $"[DACPAC] created (new) {rel}");
                            }
                        }
                        catch (Exception ex)
                        {
                            perTargetMsgs.Add($"[DACPAC] export failed: {ex.Message}");
                        }
                    }

                    item.Message = string.Join("  |  ", perTargetMsgs);
                }
                catch (Exception ex)
                {
                    item.Status  = BatchStatus.Failed;
                    item.Message = ex.Message;
                    FailCount++;
                }
            }

            // Consolidated batch zip: every source + target backup written
            // during this run, grouped by env/type inside the archive, one
            // zip per batch under today's date folder. No-op if the batch
            // wrote nothing (e.g., every item failed before any backup).
            string? batchZipPath = null;
            if (batchBackupPaths.Count > 0)
            {
                try
                {
                    var stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                    var zipName = $"Batch_{stamp}_{SourceEnv}.zip";
                    batchZipPath = _svc.Backups.CreateBatchZip(zipName, batchBackupPaths);
                }
                catch (Exception ex)
                {
                    // Best-effort — the individual .sql files are still on
                    // disk; user can zip them manually if needed.
                    _svc.Logger.Log($"Batch zip failed: {ex.Message}");
                }
            }

            // DACPAC writes files only — no git operations.
            Status = (exporter is not null && exportedPaths.Count > 0)
                ? $"Batch complete. OK: {SuccessCount}, Fail: {FailCount}. DACPAC: {exportedPaths.Count} file(s) written to {exporter.Options.RootFolder}."
                : $"Batch complete. OK: {SuccessCount}, Fail: {FailCount}.";

            if (batchZipPath is not null)
                Status += $"  Backup zip: {System.IO.Path.GetFileName(batchZipPath)}";

            var summary = $"OK: {SuccessCount} · Fail: {FailCount}";
            if (FailCount == 0 && SuccessCount > 0)        _svc.Toasts.Success("Batch complete", summary);
            else if (SuccessCount > 0 && FailCount > 0)    _svc.Toasts.Warning("Batch finished with errors", summary);
            else if (SuccessCount == 0 && FailCount > 0)   _svc.Toasts.Error("Batch failed", summary);
            else                                            _svc.Toasts.Info("Batch finished", summary);
        }
        finally
        {
            IsBusy = false;
            // Re-enable per-status filter rebuilds and run one explicit
            // rebuild so the visible list catches up to the new statuses.
            _suppressFilterRebuild = false;
            RebuildFilteredItems();
        }
    }

    /// <summary>
    /// Backup-only: captures each row's definition from source + every ticked
    /// target to the date/object backup layout. Nothing is altered on targets.
    /// </summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        if (Items.Count == 0) { Status = "No objects to back up."; return; }
        if (string.IsNullOrWhiteSpace(SourceDatabase)) { Status = "Pick source database."; return; }

        var srcConn = string.IsNullOrWhiteSpace(SourceEnv) ? null : _svc.Connections.Get(SourceEnv!, SourceDatabase!);
        var checkedTargets = Targets.Where(t => t.IsChecked).ToList();
        if (string.IsNullOrWhiteSpace(srcConn) && checkedTargets.Count == 0)
        { Status = "No source or target connection configured."; return; }

        var (backupRunStamp, stampOk) = await ResolveRunStampAsync("Name this backup");
        if (!stampOk) return;
        IsBusy = true; SuccessCount = FailCount = 0;
        // Suppress per-status filter rebuilds during the loop so rows
        // don't disappear from the visible list as they cycle through
        // Running → Success/Skipped/Failed. One final rebuild runs in
        // the finally block.
        _suppressFilterRebuild = true;
        foreach (var item in Items.ToList())
        {
            item.Status  = BatchStatus.Pending;
            item.Message = "";
        }
        // One stamp for the whole Backup click — same grouping rule as
        // Execute, just without ALTER on targets.
        try
        {
            foreach (var item in Items.ToList())
            {
                item.Status = BatchStatus.Running; item.Message = "";
                try
                {
                    var id = ObjectIdentifier.Parse(item.Name.Trim());
                    var msgs = new List<string>();
                    int hits = 0, misses = 0;

                    if (!string.IsNullOrWhiteSpace(srcConn))
                    {
                        var r = await _svc.Backup.BackupAsync(
                            srcConn!, SourceEnv!, id,
                            role: Base.It.Core.Backup.BackupRole.Source,
                            runStamp: backupRunStamp);
                        Tally(r, msgs, ref hits, ref misses, SourceEnv!);
                    }

                    foreach (var t in checkedTargets)
                    {
                        var conn = _svc.Connections.Get(t.Environment, t.Database);
                        if (string.IsNullOrWhiteSpace(conn)) continue;
                        var r = await _svc.Backup.BackupAsync(
                            conn!, t.Environment, id,
                            role: Base.It.Core.Backup.BackupRole.Target,
                            runStamp: backupRunStamp);
                        Tally(r, msgs, ref hits, ref misses, $"{t.Environment}·{t.Database}");
                    }

                    item.Message = string.Join(" | ", msgs);
                    if (hits > 0)       { item.Status = BatchStatus.Success; SuccessCount++; }
                    else if (misses > 0){ item.Status = BatchStatus.Skipped; }
                    else                { item.Status = BatchStatus.Failed; FailCount++; }
                }
                catch (Exception ex)
                {
                    item.Status = BatchStatus.Failed; item.Message = ex.Message; FailCount++;
                }
            }
            Status = $"Backup complete. Saved: {SuccessCount}, Failed: {FailCount}.";
            if (FailCount == 0 && SuccessCount > 0) _svc.Toasts.Success("Backup complete", $"{SuccessCount} saved · {FailCount} failed.");
            else if (FailCount > 0)                  _svc.Toasts.Warning("Backup finished with errors", $"{SuccessCount} saved · {FailCount} failed.");
        }
        finally
        {
            IsBusy = false;
            _suppressFilterRebuild = false;
            RebuildFilteredItems();
        }
    }

    private static void Tally(Base.It.Core.Backup.BackupOutcome r,
        List<string> msgs, ref int hits, ref int misses, string label)
    {
        switch (r.Kind)
        {
            case Base.It.Core.Backup.BackupOutcomeKind.Written:  msgs.Add($"[{label}] saved"); hits++; break;
            case Base.It.Core.Backup.BackupOutcomeKind.NotFound: msgs.Add($"[{label}] not found"); misses++; break;
            default:                                             msgs.Add($"[{label}] {r.Message}"); break;
        }
    }
}
