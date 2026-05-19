using System.Collections.ObjectModel;
using System.ComponentModel;
using Base.It.App.Services;
using Base.It.Core.Config;
using Base.It.Core.Dacpac;
using Base.It.Core.Models;
using Base.It.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Single-object push. Supports multi-target: after selecting source env +
/// source database, the user ticks one or more target endpoints from the
/// configured connections. Execute loops per target and reports per-target
/// outcomes in the status line.
/// </summary>
public sealed partial class SyncViewModel : ObservableObject
{
    private readonly AppServices _svc;

    [ObservableProperty] private string? _sourceEnv;
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string _objectName = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Idle.";
    [ObservableProperty] private string _lastZipPath = "";
    [ObservableProperty] private EnvironmentConfig? _sourceProfile;

    // UI alias for the source — backed by SourceEnv + SourceDatabase. Kept
    // in sync via _syncingEndpoint so neither side recurses into the other.
    [ObservableProperty] private EndpointPick? _selectedSourceEndpoint;

    // Unified source picker (live + snapshot), mirroring Batch's
    // BatchSourceItem model. Setting SelectedSource updates
    // SelectedSourceEndpoint (legacy code paths still read that), plus
    // SourceMode / SourceSnapshotId / SourceSnapshotDisplayName for the
    // snapshot branch in ExecuteAsync.
    [ObservableProperty] private BatchSourceItem? _selectedSource;
    [ObservableProperty] private BatchSourceMode _sourceMode = BatchSourceMode.Live;
    [ObservableProperty] private string? _sourceSnapshotId;
    [ObservableProperty] private string? _sourceSnapshotDisplayName;

    /// <summary>Convenience flag for the "from snapshot…" UI affordances.</summary>
    public bool IsSnapshotSource => SourceMode == BatchSourceMode.Snapshot;

    /// <summary>Re-entrancy guard for the SelectedSource ↔ SelectedSourceEndpoint pingpong.</summary>
    private bool _syncingSourceItem;

    // Selected saved profile. Setting it applies source + ticked-target
    // state in one shot. Setting back to null clears the selection only —
    // it doesn't undo whatever the user had picked from the profile.
    [ObservableProperty] private EndpointProfile? _selectedProfile;

    /// <summary>Live filter text driving <see cref="FilteredTargets"/>. Empty = show every target.</summary>
    [ObservableProperty] private string _targetFilter = "";

    private bool _syncingEndpoint;
    private bool _suspendRebuild;

    // DACPAC per-run opt-in — mirrors the Batch pane. Reserves the
    // configured DACPAC folder + optional git branch staging so the user
    // has a review-gated history of every successful single-object sync.
    [ObservableProperty] private bool _stageAsDacpacBranch;
    [ObservableProperty] private bool _dacpacConfigured;

    // True once we've seeded StageAsDacpacBranch from persisted settings.
    // Prevents subsequent RefreshDacpacAvailabilityAsync() calls (e.g. after
    // Settings "Save All") from overriding the user's per-run toggle.
    private bool _dacpacDefaultsApplied;

    public ObservableCollection<string> Environments { get; } = new();
    public ObservableCollection<string> Databases    { get; } = new();
    public ObservableCollection<TargetPickVm> Targets { get; } = new();

    /// <summary>Flat searchable endpoint list bound to the source AutoCompleteBox.</summary>
    public ObservableCollection<EndpointPick> Endpoints { get; } = new();

    /// <summary>User-saved source/target presets (shared with Batch).</summary>
    public ObservableCollection<EndpointProfile> Profiles { get; } = new();

    /// <summary>
    /// Live-filtered view of <see cref="Targets"/> driven by
    /// <see cref="TargetFilter"/>. The chip ItemsControl binds to this so a
    /// 50-connection group is still navigable — type a fragment of the
    /// label / env / db to narrow the wrap.
    /// </summary>
    public ObservableCollection<TargetPickVm> FilteredTargets { get; } = new();

    /// <summary>Endpoints minus every ticked target — what the SOURCE picker should show.</summary>
    public ObservableCollection<EndpointPick> SourceCandidateEndpoints { get; } = new();

    /// <summary>
    /// Unified source picker items: live endpoints + every snapshot of
    /// every endpoint's local store. Mirrors Batch's SourceCandidates.
    /// Pick a snapshot here and ExecuteAsync replays it; pick a live
    /// endpoint and ExecuteAsync fetches fresh source SQL at run time.
    /// </summary>
    public ObservableCollection<BatchSourceItem> SourceCandidates { get; } = new();

    /// <summary>Endpoints minus the source and minus every ticked target — what the "Add target" picker shows.</summary>
    public ObservableCollection<EndpointPick> TargetCandidateEndpoints { get; } = new();

    /// <summary>Live mirror of every <see cref="TargetPickVm"/> with IsChecked=true. Drives the inline chip strip.</summary>
    public ObservableCollection<TargetPickVm> CheckedTargets { get; } = new();

    /// <summary>First N of <see cref="CheckedTargets"/> — rendered as chips inline in the toolbar.</summary>
    public ObservableCollection<TargetPickVm> CheckedTargetsVisible { get; } = new();

    /// <summary>Tail beyond the visible cap — surfaced via the "+N more" flyout.</summary>
    public ObservableCollection<TargetPickVm> CheckedTargetsOverflow { get; } = new();

    private const int VisibleTargetChipsMax = 3;

    public int  CheckedTargetsOverflowCount => CheckedTargetsOverflow.Count;
    public bool HasCheckedTargetsOverflow   => CheckedTargetsOverflow.Count > 0;

    /// <summary>
    /// Pick proxy bound to the "Add target" AutoCompleteBox. Setting it
    /// ticks the matching <see cref="TargetPickVm"/> and resets to null
    /// so the picker is ready for the next add.
    /// </summary>
    [ObservableProperty] private EndpointPick? _nextTargetEndpoint;

    /// <summary>Swap is meaningful only when there's exactly one ticked target to swap with.</summary>
    public bool CanSwap =>
        SelectedSourceEndpoint is not null && Targets.Count(t => t.IsChecked) == 1;

    /// <summary>Swap hides entirely once the user picks more than one target.</summary>
    public bool IsSwapVisible => Targets.Count(t => t.IsChecked) <= 1;

    public int TargetSelectedCount => Targets.Count(t => t.IsChecked);
    public int TargetTotalCount    => Targets.Count;

    public SyncViewModel(AppServices svc)
    {
        _svc = svc;
        Reload();
        _ = RefreshDacpacAvailabilityAsync();
    }

    /// <summary>
    /// Mirror of <see cref="BatchViewModel.RefreshDacpacAvailabilityAsync"/>.
    /// Called on construction and whenever the user saves DACPAC settings
    /// (via the ConnectionsChanged pipeline in MainWindow). Syncs the
    /// Sync-pane's local checkbox to the globally-configured default.
    /// </summary>
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

    public void Reload()
    {
        Environments.Clear();
        foreach (var e in EnvironmentListProvider.Environments(_svc)) Environments.Add(e);
        Databases.Clear();
        foreach (var d in EnvironmentListProvider.Databases(_svc)) Databases.Add(d);

        // Flat endpoint list for the AutoCompleteBox source picker.
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

    /// <summary>Pull the persisted profile list into the bound collection. Preserves selection by Id.</summary>
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

        // Mirror the change into the new BatchSourceItem-based picker so
        // the AutoCompleteBox visually reflects the new source after a
        // profile apply / swap / programmatic SourceEnv update. Without
        // this the dropdown still shows the previous selection because
        // SelectedSource only updates when the *user* clicks an item.
        // Only sets when the matching live (non-snapshot) item exists in
        // SourceCandidates — preserves snapshot picks chosen by hand.
        if (match is not null && (SelectedSource is null || SelectedSource.IsSnapshot
            || !ReferenceEquals(SelectedSource.Endpoint, match)))
        {
            var liveItem = SourceCandidates.FirstOrDefault(s =>
                !s.IsSnapshot && ReferenceEquals(s.Endpoint, match));
            if (liveItem is not null && !ReferenceEquals(liveItem, SelectedSource))
            {
                _syncingSourceItem = true;
                try { SelectedSource = liveItem; }
                finally { _syncingSourceItem = false; }
                SourceMode = BatchSourceMode.Live;
                SourceSnapshotId = null;
                SourceSnapshotDisplayName = null;
            }
        }
    }

    /// <summary>
    /// Restore source + target chip state from a saved profile. Atomic — the
    /// targets list is rebuilt once, then chips matching the profile keys are
    /// re-checked. No-op when the profile's source isn't visible under the
    /// active connection group (the source picker still shows "no match").
    /// </summary>
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

    /// <summary>
    /// Rebuild the target list from the active connection group (or every
    /// connection when no group is active), minus the source endpoint.
    /// Preserves existing IsChecked state across rebuilds.
    /// </summary>
    private void RebuildTargets()
    {
        var previouslyChecked = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();

        // Detach the IsChecked listener before clearing so we don't leak.
        foreach (var t in Targets) t.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Clear();
        CheckedTargets.Clear();

        // Live source can't sync to itself — target would overwrite the
        // source mid-run. Snapshot source CAN: the snapshot is a frozen
        // disk copy, applying it to the live same-DB is the legitimate
        // "restore from snapshot" workflow. Same branch as the target-
        // candidate filter in RebuildEndpointCandidatesCore. Without it,
        // the user could see the same-DB row in the TargetCandidate
        // dropdown but clicking it would do nothing — no matching
        // TargetPickVm in this Targets list for OnNextTargetEndpoint to
        // tick.
        var excludeSourceFromTargets = SourceMode == BatchSourceMode.Live;

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            if (excludeSourceFromTargets
                && string.Equals(cfg.Environment, SourceEnv, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cfg.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
                continue;

            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains($"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}"));
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
    /// Recompute the source / target candidate lists. Deferred to the next
    /// dispatcher tick so we don't synchronously clear+rebuild the
    /// ItemsSource of an AutoCompleteBox that's mid-callback (which crashes).
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

        // Live source can't sync to itself (target would overwrite the
        // source mid-run). Snapshot source CAN — replaying a stored
        // snapshot back onto its own live DB is the "restore from
        // snapshot" workflow. Mirrors Batch's relaxed rule.
        var excludeSourceFromTargets = SourceMode == BatchSourceMode.Live;

        SourceCandidateEndpoints.Clear();
        TargetCandidateEndpoints.Clear();
        foreach (var ep in Endpoints)
        {
            if (!MatchesAnyTicked(ep)) SourceCandidateEndpoints.Add(ep);
            if (!MatchesAnyTicked(ep) && !(excludeSourceFromTargets && MatchesSource(ep)))
                                       TargetCandidateEndpoints.Add(ep);
        }

        RebuildSourceCandidates(MatchesAnyTicked);
    }

    /// <summary>
    /// SourceMode flipping live↔snapshot needs to refresh BOTH the master
    /// Targets list AND the candidate dropdown. The Targets rebuild is the
    /// load-bearing one: without it, the same-DB row stays out of the
    /// master list because RebuildTargets ran while SourceMode was still
    /// Live (it flips here, *after* SelectedSourceEndpoint), and the
    /// dropdown click would find no matching TargetPickVm to tick.
    /// See Batch's matching hook for the same fix.
    /// </summary>
    partial void OnSourceModeChanged(BatchSourceMode value)
    {
        RebuildTargets();
        RebuildEndpointCandidates();
        OnPropertyChanged(nameof(IsSnapshotSource));
    }

    /// <summary>
    /// Build the unified picker list: live endpoint per row, plus one
    /// row per stored snapshot. Snapshots of a ticked-target endpoint
    /// stay listed (legit "restore the same DB from its snapshot" case).
    /// </summary>
    private void RebuildSourceCandidates(Func<EndpointPick, bool> matchesTicked)
    {
        var keepKey = SelectedSource?.Key;

        SourceCandidates.Clear();
        foreach (var ep in Endpoints)
        {
            if (!matchesTicked(ep))
                SourceCandidates.Add(new BatchSourceItem(ep, Snapshot: null));

            // Snapshots from the local store. Cheap (Directory.Exists)
            // when a store doesn't exist for an endpoint yet.
            try
            {
                var store = _svc.OpenSchemaStore(ep.Environment, ep.Database);
                foreach (var snap in store.ListSnapshots())
                    SourceCandidates.Add(new BatchSourceItem(ep, snap));
            }
            catch { /* store unreadable — never blow up the picker */ }
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
    /// SelectedSource changes drive SelectedSourceEndpoint (legacy
    /// binding) plus SourceMode / snapshot id so ExecuteAsync can
    /// branch. Mirrors Batch's OnSelectedSourceChanged.
    /// </summary>
    partial void OnSelectedSourceChanged(BatchSourceItem? value)
    {
        if (_syncingSourceItem) return;
        if (value is null)
        {
            SourceMode = BatchSourceMode.Live;
            SourceSnapshotId = null;
            SourceSnapshotDisplayName = null;
            return;
        }

        _syncingSourceItem = true;
        try
        {
            // Update legacy endpoint binding so existing code paths
            // (SourceEnv/SourceDatabase, target rebuild) see the new
            // source. SourceMode is set *after* so the live-source
            // exclusion of same-DB targets evaluates correctly when the
            // user has just flipped to a snapshot of the same endpoint.
            SelectedSourceEndpoint = value.Endpoint;
        }
        finally { _syncingSourceItem = false; }

        SourceMode = value.IsSnapshot ? BatchSourceMode.Snapshot : BatchSourceMode.Live;
        SourceSnapshotId = value.Snapshot?.Id;
        SourceSnapshotDisplayName = value.Snapshot?.DisplayName;
    }

    /// <summary>
    /// Adding a target via the "Add target" picker. Ticks the matching
    /// target and resets the picker back to null.
    /// </summary>
    partial void OnNextTargetEndpointChanged(EndpointPick? value)
    {
        if (value is null) return;
        var t = Targets.FirstOrDefault(t =>
            string.Equals(t.Environment, value.Environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Database,    value.Database,    StringComparison.OrdinalIgnoreCase));
        if (t is not null && !t.IsChecked) t.IsChecked = true;
        NextTargetEndpoint = null;
    }

    /// <summary>Remove a single target from the selected set. Wired from the × on each chip in the view.</summary>
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

    /// <summary>Tick every target currently visible in <see cref="FilteredTargets"/>. Filtered-out chips are left alone.</summary>
    [RelayCommand]
    private void SelectAllVisibleTargets()
    {
        foreach (var t in FilteredTargets) t.IsChecked = true;
    }

    /// <summary>Untick every target — including filtered-out ones — so the user has a single clean reset action.</summary>
    [RelayCommand]
    private void ClearTargets()
    {
        foreach (var t in Targets) t.IsChecked = false;
    }

    /// <summary>Save current source + ticked target state as a new profile, prompting for a name.</summary>
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

    /// <summary>
    /// Swap source with the (single) ticked target. Disabled unless exactly
    /// one target is ticked — the operation has no obvious meaning otherwise.
    /// </summary>
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

        // Re-check the chip that matches the old source — that's the new target.
        var newKey = $"{oldEnv?.ToUpperInvariant()}|{oldDb?.ToUpperInvariant()}";
        foreach (var x in Targets) x.IsChecked = x.Key == newKey;
        SyncSelectedEndpoint();
    }

    private string SuggestProfileName()
    {
        var src = $"{SourceEnv}/{SourceDatabase}";
        var first = Targets.FirstOrDefault(t => t.IsChecked);
        return first is null
            ? src
            : $"{SourceDatabase}: {SourceEnv} → {first.Environment}";
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase) ||
            string.IsNullOrWhiteSpace(ObjectName))
        {
            Status = "Pick source env, source database, and object name.";
            _svc.Toasts.Warning("Missing fields", "Pick source env, database, and object name before running.");
            return;
        }

        var checkedTargets = Targets.Where(t => t.IsChecked).ToList();
        if (checkedTargets.Count == 0)
        {
            Status = "Pick at least one target.";
            _svc.Toasts.Warning("No targets", "Tick one or more target connections before syncing.");
            return;
        }

        // ── Live vs snapshot source ──────────────────────────────────
        // Live: fetch the source connection string up front and the
        // ExecuteAsync loop calls SyncAsync (live source backup + fetch).
        // Snapshot: open the schema store and resolve the object's stored
        // SQL into a SqlObject; the loop calls SyncFromDefinitionAsync per
        // target (the snapshot already IS the source-of-truth — no live
        // connection to the source DB needed). Skips the DACPAC export
        // branch entirely (the snapshot already serves as the staged copy).
        string?    srcConn       = null;
        SqlObject? snapshotSource = null;
        string     sourceLabel    = $"{SourceEnv}/{SourceDatabase}";

        if (SourceMode == BatchSourceMode.Snapshot)
        {
            if (string.IsNullOrWhiteSpace(SourceSnapshotId))
            {
                Status = "Pick a snapshot in the source dropdown.";
                _svc.Toasts.Warning("No snapshot", "Source mode is snapshot but no snapshot is selected.");
                return;
            }
            try
            {
                var store = _svc.OpenSchemaStore(SourceEnv!, SourceDatabase!);
                var snap  = await store.ReadSnapshotAsync(SourceSnapshotId!);
                if (snap is null)
                {
                    Status = "Snapshot is missing from the schema store.";
                    _svc.Toasts.Error("Snapshot not found", $"Snapshot {SourceSnapshotId} not in store.");
                    return;
                }
                var entry = snap.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, ObjectName!.Trim(), StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    Status = $"'{ObjectName}' isn't in the selected snapshot.";
                    _svc.Toasts.Warning("Not in snapshot",
                        $"'{ObjectName}' wasn't captured in this snapshot.");
                    return;
                }
                var sql = await store.ReadObjectAsync(entry.Hash);
                if (string.IsNullOrWhiteSpace(sql))
                {
                    Status = "Snapshot entry has no readable SQL.";
                    _svc.Toasts.Error("Empty definition", $"'{ObjectName}' has no SQL in the snapshot.");
                    return;
                }
                snapshotSource = new SqlObject(
                    new ObjectIdentifier(entry.Schema, entry.Name),
                    entry.Kind,
                    sql!,
                    entry.Hash);
                var snapLabel = string.IsNullOrWhiteSpace(SourceSnapshotDisplayName)
                    ? "snapshot"
                    : SourceSnapshotDisplayName;
                sourceLabel = $"{SourceEnv}/{SourceDatabase} @ {snapLabel}";
            }
            catch (Exception ex)
            {
                Status = $"Could not read snapshot: {ex.Message}";
                _svc.Toasts.Error("Snapshot read failed", ex.Message);
                return;
            }
        }
        else
        {
            srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!);
            if (string.IsNullOrWhiteSpace(srcConn))
            {
                Status = "No connection string for source.";
                _svc.Toasts.Error("No source connection", $"{SourceEnv}·{SourceDatabase} isn't configured.");
                return;
            }
        }

        // Major-action gate: Execute mutates every ticked target. Confirm
        // before running so a stray click can't push a stored procedure
        // to PROD. Mirrors the Batch screen's confirm dialog so the
        // single-execution path is held to the same safety standard.
        // Object name + target count + ALTER/CREATE warning are spelled
        // out so the user sees the blast radius before saying yes.
        var targetsLabel = checkedTargets.Count == 1 ? "target" : "targets";
        var targetSummary = checkedTargets.Count <= 3
            ? string.Join(", ", checkedTargets.Select(t => $"{t.Environment}/{t.Database}"))
            : $"{checkedTargets.Count} {targetsLabel}";
        var confirmed = await ConfirmDialog.AskAsync(
            title:       "Execute sync?",
            message:     $"This will sync '{ObjectName!.Trim()}' from " +
                         $"{sourceLabel} to {targetSummary}. " +
                         $"Existing objects will be ALTERED; missing ones will be CREATED. Continue?",
            primaryText: "Execute",
            cancelText:  "Cancel");
        if (!confirmed)
        {
            Status = "Execute cancelled.";
            return;
        }

        // DACPAC export doesn't apply when source is a snapshot — the
        // snapshot already IS the stored copy, no need to re-serialise.
        var exporter = SourceMode == BatchSourceMode.Snapshot
            ? null
            : await _svc.TryBuildDacpacExporterAsync();
        var exportedPaths = new List<string>();
        bool anyTargetSucceeded = false;

        IsBusy = true; LastZipPath = "";
        try
        {
            var id = ObjectIdentifier.Parse(ObjectName.Trim());
            var parts = new List<string>();
            int ok = 0, fail = 0, notFound = 0;

            // One run-stamp groups every backup file (source + each
            // target) under the same dated folder. SyncAsync writes a
            // source-side backup on every call by default; we capture
            // it exactly once here and pass captureSourceBackup=false
            // through the loop so the source folder doesn't accumulate
            // N identical copies.
            // In snapshot mode there's no live source to back up — the
            // snapshot is itself the persisted copy.
            var runStamp = Base.It.Core.Backup.FileBackupStore.NewRunStamp();
            string? sourceBackupPath = null;
            if (SourceMode == BatchSourceMode.Live)
            {
                try
                {
                    var srcOutcome = await _svc.Backup.BackupAsync(
                        srcConn!, SourceEnv!, id,
                        role: Base.It.Core.Backup.BackupRole.Source,
                        runStamp: runStamp);
                    if (srcOutcome.Kind == Base.It.Core.Backup.BackupOutcomeKind.Written)
                        sourceBackupPath = srcOutcome.FilePath;
                }
                catch { /* best-effort — sync continues even if the pre-capture failed */ }
            }

            foreach (var t in checkedTargets)
            {
                var tgtConn = _svc.Connections.Get(t.Environment, t.Database);
                if (string.IsNullOrWhiteSpace(tgtConn))
                {
                    parts.Add($"[{t.Environment}·{t.Database}] no connection"); fail++;
                    continue;
                }
                try
                {
                    Base.It.Core.Sync.SyncResult r;
                    if (SourceMode == BatchSourceMode.Snapshot)
                    {
                        // Snapshot path: apply the pre-fetched SqlObject.
                        // No live source connection is touched.
                        r = await _svc.Sync.SyncFromDefinitionAsync(
                            tgtConn!, snapshotSource!,
                            sourceLabel: sourceLabel,
                            targetEnv:   t.Environment,
                            ct:          default,
                            zipPair:     true,
                            runStamp:    runStamp);
                    }
                    else
                    {
                        r = await _svc.Sync.SyncAsync(
                            srcConn!, tgtConn!, id, SourceEnv!, t.Environment,
                            ct: default, zipPair: true,
                            captureSourceBackup: false,
                            runStamp: runStamp);
                    }
                    switch (r.Status)
                    {
                        case SyncStatus.Success:
                            parts.Add($"[{t.Environment}·{t.Database}] ok"); ok++;
                            anyTargetSucceeded = true;
                            if (string.IsNullOrWhiteSpace(LastZipPath)) LastZipPath = r.ZipPath ?? "";
                            break;
                        case SyncStatus.NotFound:
                            parts.Add($"[{t.Environment}·{t.Database}] not found"); notFound++;
                            break;
                        default:
                            parts.Add($"[{t.Environment}·{t.Database}] failed: {r.Message}"); fail++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    parts.Add($"[{t.Environment}·{t.Database}] error: {ex.Message}"); fail++;
                }
            }

            // DACPAC export — gated on the per-run checkbox. Unchecked
            // means don't touch the DACPAC folder at all. Runs once per
            // sync (not per target) since the source definition is the
            // same for every target, and only when at least one target
            // succeeded so a pure-fail run doesn't pollute the tree.
            if (exporter is not null && anyTargetSucceeded && StageAsDacpacBranch)
            {
                try
                {
                    // Routed through AppServices so the trigger-inline
                    // policy stays in one place: a trigger with no
                    // existing standalone file in the SSDT tree gets
                    // folded into its parent table's file instead of
                    // creating Triggers2/.
                    var result = await _svc.ExportToDacpacAsync(exporter, srcConn!, id);
                    if (result.Path is not null)
                    {
                        exportedPaths.Add(result.Path);
                        parts.Add($"[DACPAC] {System.IO.Path.GetRelativePath(exporter.Options.RootFolder, result.Path)}");
                    }
                }
                catch (Exception ex) { parts.Add($"[DACPAC] export failed: {ex.Message}"); }
            }

            // DACPAC now writes files only — no git operations. Users who
            // want a commit/branch for this batch of writes do it
            // themselves in their git client.

            Status = $"{ok} ok · {fail} failed · {notFound} not-found   —   {string.Join("  ", parts)}";

            var summary = $"{ok} ok · {fail} failed · {notFound} not-found";
            if (fail == 0 && ok > 0)       _svc.Toasts.Success("Sync complete", summary);
            else if (ok > 0 && fail > 0)   _svc.Toasts.Warning("Sync finished with errors", summary);
            else if (ok == 0)              _svc.Toasts.Error("Sync failed", summary);
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Backup-only: captures source and every checked target (when connectable)
    /// to the date/object backup layout without executing anything on targets.
    /// </summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceDatabase) || string.IsNullOrWhiteSpace(ObjectName))
        {
            Status = "Pick a source database and object name.";
            return;
        }

        IsBusy = true;
        try
        {
            var id     = ObjectIdentifier.Parse(ObjectName.Trim());
            var parts  = new List<string>();
            // One stamp groups source + every target in this Backup
            // click into the same dated folder structure.
            var runStamp = Base.It.Core.Backup.FileBackupStore.NewRunStamp();

            if (!string.IsNullOrWhiteSpace(SourceEnv))
            {
                var conn = _svc.Connections.Get(SourceEnv!, SourceDatabase!);
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    var r = await _svc.Backup.BackupAsync(
                        conn!, SourceEnv!, id,
                        role: Base.It.Core.Backup.BackupRole.Source,
                        runStamp: runStamp);
                    parts.Add(FormatPart(SourceEnv!, r));
                }
            }

            foreach (var t in Targets.Where(t => t.IsChecked))
            {
                var conn = _svc.Connections.Get(t.Environment, t.Database);
                if (string.IsNullOrWhiteSpace(conn)) continue;
                var r = await _svc.Backup.BackupAsync(
                    conn!, t.Environment, id,
                    role: Base.It.Core.Backup.BackupRole.Target,
                    runStamp: runStamp);
                parts.Add(FormatPart($"{t.Environment}·{t.Database}", r));
            }

            Status = parts.Count == 0 ? "Nothing to back up." : string.Join("   ", parts);
            if (parts.Count == 0) _svc.Toasts.Warning("Backup skipped", "No reachable sources or targets.");
            else                  _svc.Toasts.Success("Backup complete", $"{parts.Count} file(s) written.");
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            _svc.Toasts.Error("Backup failed", ex.Message);
        }
        finally               { IsBusy = false; }
    }

    private static string FormatPart(string label, Base.It.Core.Backup.BackupOutcome r) => r.Kind switch
    {
        Base.It.Core.Backup.BackupOutcomeKind.Written  => $"[{label}] saved",
        Base.It.Core.Backup.BackupOutcomeKind.NotFound => $"[{label}] not found",
        _                                              => $"[{label}] {r.Message}"
    };

    /// <summary>
    /// Build a preview of <see cref="ObjectName"/> across the source + every
    /// ticked target — same shape Batch uses, so the preview window can be
    /// shared. Returns null when the source isn't picked, the object name is
    /// blank, or no target is ticked. Connection strings are resolved here so
    /// the preview window keeps working even after the source / target pick
    /// changes underneath it.
    /// </summary>
    public async Task<BatchPreviewViewModel?> BuildPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase)) return null;
        if (string.IsNullOrWhiteSpace(ObjectName)) return null;

        // Targets are always live — Sync writes to live DBs. Build the
        // target endpoint list once and share it across both source
        // branches below.
        var targetEndpoints = new List<PreviewEndpoint>();
        foreach (var t in Targets.Where(t => t.IsChecked))
        {
            var tgtConn = _svc.Connections.Get(t.Environment, t.Database) ?? "";
            var profile = _svc.Connections.GetProfile(t.Environment, t.Database);
            targetEndpoints.Add(new PreviewEndpoint(
                Label:            $"Target · {t.Environment} / {t.Database}",
                Color:            profile?.Color,
                ConnectionString: tgtConn));
        }
        if (targetEndpoints.Count == 0) return null;  // source-only preview is pointless

        // Snapshot source → pull the literal SQL from the local schema
        // store and use ForFileAndTargets (same pattern Batch uses).
        // Skips the live-source fetch entirely so a "Snapshot of PROD"
        // source can drive a preview even when live PROD is unreachable.
        if (SourceMode == BatchSourceMode.Snapshot
            && !string.IsNullOrWhiteSpace(SourceSnapshotId))
        {
            try
            {
                var store = _svc.OpenSchemaStore(SourceEnv!, SourceDatabase!);
                var snap  = await store.ReadSnapshotAsync(SourceSnapshotId!);
                var entry = snap?.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, ObjectName!.Trim(), StringComparison.OrdinalIgnoreCase));
                var sql = entry is null ? null : await store.ReadObjectAsync(entry.Hash);
                var snapLabel = string.IsNullOrWhiteSpace(SourceSnapshotDisplayName)
                    ? "snapshot"
                    : SourceSnapshotDisplayName;
                var sourceText = string.IsNullOrWhiteSpace(sql)
                    ? $"-- '{ObjectName}' is not present in the selected snapshot."
                    : sql!;
                return BatchPreviewViewModel.ForFileAndTargets(
                    svc:         _svc,
                    sourceLabel: $"Source · {SourceEnv} / {SourceDatabase} @ {snapLabel}",
                    fileContent: sourceText,
                    objectName:  ObjectName!.Trim(),
                    targets:     targetEndpoints);
            }
            catch
            {
                // Fall through to live-source path so the user at least sees
                // target panes if the snapshot read failed.
            }
        }

        // Live source path — original behaviour.
        var endpoints = new List<PreviewEndpoint>();
        var srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!) ?? "";
        endpoints.Add(new PreviewEndpoint(
            Label:            $"Source · {SourceEnv} / {SourceDatabase}",
            Color:            SourceProfile?.Color,
            ConnectionString: srcConn));
        endpoints.AddRange(targetEndpoints);
        return new BatchPreviewViewModel(_svc, ObjectName!.Trim(), endpoints);
    }

    /// <summary>
    /// Inline preview state, bound to the embedded PaneDiffView on the
    /// Sync screen. Setting this triggers the view to re-render its panes
    /// (via PaneDiffView's DataContextChanged → Bind path). Null hides
    /// the inline section entirely.
    /// </summary>
    [ObservableProperty] private BatchPreviewViewModel? _preview;

    /// <summary>True when there's a live inline preview to display.</summary>
    public bool HasInlinePreview => Preview is not null;

    partial void OnPreviewChanged(BatchPreviewViewModel? value)
        => OnPropertyChanged(nameof(HasInlinePreview));

    /// <summary>
    /// Compare command — builds an inline preview (source + every ticked
    /// target) and runs the load so panes render side-by-side on the Sync
    /// screen itself. Replaces the old per-row "Preview" button, which
    /// opened a separate window: the user wanted the comparison and the
    /// sync action on the same screen.
    /// </summary>
    [RelayCommand]
    private async Task CompareAsync()
    {
        var preview = await BuildPreviewAsync();
        if (preview is null)
        {
            _svc.Toasts.Warning("Nothing to compare",
                "Pick source, type an object name, and tick at least one target.");
            return;
        }
        Preview = preview;
        // Kick the load so panes actually populate. PaneDiffView reacts
        // to the resulting Panes.CollectionChanged and re-renders.
        await preview.LoadAsync();
    }

    /// <summary>
    /// Synchronous wrapper kept for backwards compatibility with the
    /// PreviewRequested event subscribers (if any external view still
    /// expects to open the standalone window). New code should call
    /// CompareCommand instead, which renders inline.
    /// </summary>
    public BatchPreviewViewModel? BuildPreview()
        => BuildPreviewAsync().GetAwaiter().GetResult();

    /// <summary>Legacy event — kept so the existing SyncView code-behind that opens BatchPreviewWindow doesn't break compilation. The inline merge supersedes it.</summary>
    public event Action<BatchPreviewViewModel>? PreviewRequested;
}
