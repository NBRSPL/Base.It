using System.Collections.ObjectModel;
using Avalonia.Threading;
using Base.It.App.Services;
using Base.It.Core.Config;
using Base.It.Core.Drift;
using Base.It.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Per-target drift state kept on every <see cref="DriftRowVm"/>. One
/// entry exists per target env+db the row has been reported against.
/// The <see cref="SeenThisTick"/> flag lets the watcher prune rows that
/// are no longer in a target's auto-discovered plan.
/// </summary>
public sealed class TargetDriftState
{
    public required string    Environment { get; init; }
    public required string    Database    { get; init; }
    public required DriftKind Kind        { get; set; }
    public string?           TargetHash  { get; set; }
    public string?           Message     { get; set; }
    public bool              SeenThisTick { get; set; }
}

/// <summary>
/// A chip on a drift row — one per target where the object is not InSync.
/// The view binds text (env label) + a brush keyed off <see cref="Kind"/>
/// via <see cref="DriftStatusBrushConverter"/>.
/// </summary>
public sealed record TargetDriftTag(string Label, string Kind);

/// <summary>
/// Row model for the live drift grid. Aggregates per-target drift states
/// so a single object can render once with chips showing each target it
/// differs from.
/// </summary>
public sealed partial class DriftRowVm : ObservableObject
{
    [ObservableProperty] private string _objectName = "";
    [ObservableProperty] private string _sourceHash = "";
    [ObservableProperty] private string _primaryStatus = "";      // aggregate, first non-InSync kind
    [ObservableProperty] private string _message       = "";      // first non-empty message across targets
    [ObservableProperty] private bool   _isSelected    = true;

    /// <summary>Object type kept on the row so the UI can slot the row into the right type section.</summary>
    public SqlObjectType ObjectType { get; set; }

    /// <summary>Per-target state keyed by <see cref="TargetRoute.Key"/>.</summary>
    public Dictionary<string, TargetDriftState> TargetStates { get; } = new();

    /// <summary>One chip per target where kind != InSync. Bound by the row template.</summary>
    public ObservableCollection<TargetDriftTag> TargetTags { get; } = new();

    /// <summary>True when at least one target is Different or MissingInTarget.</summary>
    public bool IsSyncable =>
        TargetStates.Values.Any(s => s.Kind is DriftKind.Different or DriftKind.MissingInTarget);

    /// <summary>True only when every target we've heard from reports InSync.</summary>
    public bool IsAllInSync =>
        TargetStates.Count > 0 && TargetStates.Values.All(s => s.Kind == DriftKind.InSync);

    /// <summary>Rebuild the chip collection + aggregate status/message from the per-target dict.</summary>
    public void RebuildAggregate()
    {
        // Chips: show non-InSync targets, coloured by kind.
        TargetTags.Clear();
        foreach (var kv in TargetStates)
        {
            if (kv.Value.Kind == DriftKind.InSync) continue;
            TargetTags.Add(new TargetDriftTag(kv.Value.Environment, kv.Value.Kind.ToString()));
        }

        // Aggregate status + message prioritise Error > MissingInTarget > Different.
        var priority = new[] { DriftKind.Error, DriftKind.MissingInTarget, DriftKind.Different, DriftKind.MissingInSource, DriftKind.InSync };
        TargetDriftState? winner = null;
        foreach (var k in priority)
        {
            winner = TargetStates.Values.FirstOrDefault(s => s.Kind == k);
            if (winner is not null) break;
        }
        PrimaryStatus = winner?.Kind.ToString() ?? "";
        Message       = TargetStates.Values.Select(s => s.Message)
                                           .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "";
    }
}

/// <summary>
/// A single type-grouped section (Stored Procedures, Functions, Triggers,
/// Tables, Views). The fixed set + order is enforced in the
/// <see cref="WatchViewModel"/> constructor. Sections with zero rows are
/// kept visible so the structure stays predictable across ticks.
/// </summary>
public sealed partial class WatchSectionVm : ObservableObject
{
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private int  _count;

    /// <summary>
    /// Toggle in the section header. When flipped the VM writes the new
    /// object-types list back to the group store, so next tick's plan
    /// only scans enabled sections. Disabling immediately clears the
    /// section's rows so stale state doesn't linger.
    /// </summary>
    [ObservableProperty] private bool _isScanEnabled = true;

    public string Title { get; }
    /// <summary>Which SqlObjectType values this section represents (for persisting).</summary>
    public IReadOnlyList<SqlObjectType> SqlTypes { get; }
    public Predicate<SqlObjectType> Matches { get; }
    public ObservableCollection<DriftRowVm> Rows { get; } = new();

    public bool IsEmpty => Count == 0;

    public WatchSectionVm(string title, IReadOnlyList<SqlObjectType> types, Predicate<SqlObjectType> matches)
    {
        Title = title; SqlTypes = types; Matches = matches;
        Rows.CollectionChanged += (_, __) => Count = Rows.Count;
    }

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(IsEmpty));
}

/// <summary>
/// Sidebar item — one per watch group. Displays the multi-target route and
/// a red pill when any of the group's watchers has reported drift.
/// </summary>
public sealed partial class WatchGroupItemVm : ObservableObject
{
    public WatchGroup Group { get; private set; }

    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private int  _changedCount;
    [ObservableProperty] private int  _errorCount;
    [ObservableProperty] private DateTime? _lastUpdatedUtc;

    public WatchGroupItemVm(WatchGroup g) { Group = g; }

    public void Replace(WatchGroup g)
    {
        Group = g;
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(DotBrush));
        OnPropertyChanged(nameof(NameBrush));
    }

    /// <summary>Route segment: src→T1, T2… capped for readability in narrow sidebars.</summary>
    private string RouteLabel()
    {
        if (Group.Targets.Count == 0) return Group.SourceEnv;
        var targets = Group.Targets.Select(t => t.Environment).Distinct().ToList();
        return targets.Count <= 2
            ? $"{Group.SourceEnv}→{string.Join(",", targets)}"
            : $"{Group.SourceEnv}→{targets[0]},+{targets.Count - 1}";
    }

    public string Display
    {
        get
        {
            var scope   = Group.Objects.Count == 0 ? "  (all)" : "";
            var changes = HasChanges ? $"  ({ChangedCount} changed)" : "";
            return $"{Group.Name}   [{RouteLabel()}]{scope}{changes}";
        }
    }

    public string DotBrush   => HasChanges ? "#E53935" : (Group.Enabled ? "#4CAF50" : "#9E9E9E");
    public string NameBrush  => HasChanges ? "#E53935" : "#FFFFFF";

    partial void OnHasChangesChanged(bool value)  { OnPropertyChanged(nameof(Display)); OnPropertyChanged(nameof(DotBrush)); OnPropertyChanged(nameof(NameBrush)); }
    partial void OnChangedCountChanged(int value) => OnPropertyChanged(nameof(Display));
    partial void OnErrorCountChanged(int value)   => OnPropertyChanged(nameof(Display));
}

/// <summary>
/// Main VM for the Watch pane. Owns N <see cref="ChangeWatcher"/>s per
/// group — one per (group × target). Each watcher emits events tagged with
/// its own (env, db); the VM merges those into per-row per-target states
/// so a single row can show chips for every target it differs from.
/// </summary>
public sealed partial class WatchViewModel : ObservableObject
{
    private readonly AppServices _svc;
    // Composite-keyed runs: (group.Id, targetRoute.Key) → watcher run.
    private readonly Dictionary<(Guid Group, string Target), WatcherRun> _runs = new();
    // Per-group aggregate counters — summed across every target's ticks.
    private readonly Dictionary<Guid, GroupAggregate> _groupAgg = new();
    private bool _initialized;

    [ObservableProperty] private WatchGroupItemVm? _selected;
    [ObservableProperty] private string _status = "No groups. Create one to start watching.";
    [ObservableProperty] private string _headerTitle    = "";
    [ObservableProperty] private string _headerSubtitle = "";
    [ObservableProperty] private string _lastUpdatedText = "";

    /// <summary>
    /// Mirror of <see cref="DacpacExportOptions.IsUsable"/>. Stage / Stage All
    /// only make sense when the export is configured, so the view hides them
    /// when this is false instead of letting the user click into a toast
    /// error. Refreshed on construction and whenever Settings fires
    /// ConnectionsChanged.
    /// </summary>
    [ObservableProperty] private bool _dacpacConfigured;

    /// <summary>Label for the Start/Stop button — flips with the selected group's enabled state.</summary>
    public string StartStopLabel => (Selected?.Group.Enabled ?? false) ? "Stop" : "Start";
    /// <summary>Whether the selected group is currently running — used by the view to swap button class.</summary>
    public bool IsSelectedRunning => Selected?.Group.Enabled ?? false;

    public ObservableCollection<WatchGroupItemVm> Groups   { get; } = new();
    public ObservableCollection<WatchSectionVm>   Sections { get; } = new();

    /// <summary>Flat projection over every section's rows. Used by Stage / Send-to-Batch.</summary>
    public IEnumerable<DriftRowVm> LiveRows => Sections.SelectMany(s => s.Rows);

    /// <summary>Raised when the user hits "Send Changed to Batch". MainWindow subscribes to navigate + populate the Batch tab.</summary>
    public event Action<IReadOnlyList<string>, string, string, string>? SendToBatchRequested;

    public WatchViewModel(AppServices svc)
    {
        _svc = svc;
        Sections.Add(new("Stored Procedures",
            new[] { SqlObjectType.StoredProcedure },
            t => t == SqlObjectType.StoredProcedure));
        Sections.Add(new("Functions",
            new[] { SqlObjectType.ScalarFunction, SqlObjectType.InlineTableFunction, SqlObjectType.TableValuedFunction },
            t => t is SqlObjectType.ScalarFunction
                   or SqlObjectType.InlineTableFunction
                   or SqlObjectType.TableValuedFunction));
        Sections.Add(new("Triggers",
            new[] { SqlObjectType.Trigger },
            t => t == SqlObjectType.Trigger));
        Sections.Add(new("Tables",
            new[] { SqlObjectType.Table },
            t => t == SqlObjectType.Table));
        Sections.Add(new("Views",
            new[] { SqlObjectType.View },
            t => t == SqlObjectType.View));

        // Live-apply: when the user flips a section's toggle, persist the
        // new object-types list back to the group so the next tick scans
        // the right set. Immediate UI effect: rows clear on disable.
        foreach (var s in Sections)
            s.PropertyChanged += OnSectionPropertyChanged;

        _ = RefreshDacpacAvailabilityAsync();
    }

    /// <summary>
    /// Re-read DACPAC settings so Stage buttons show/hide in sync with the
    /// Settings pane. Called on construction and whenever Settings fires
    /// its ConnectionsChanged broadcast.
    /// </summary>
    public async Task RefreshDacpacAvailabilityAsync()
    {
        var opts = await _svc.DacpacOptions.LoadAsync();
        DacpacConfigured = opts.IsUsable;
    }

    /// <summary>
    /// Handle a section's scan-toggle flip: rewrite the group's
    /// <see cref="WatchGroup.ObjectTypes"/> and persist. Rows are cleared
    /// immediately for the disabled section — next tick just won't refill
    /// them. Persists to disk fire-and-forget.
    /// </summary>
    private void OnSectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WatchSectionVm.IsScanEnabled)) return;
        if (sender is not WatchSectionVm section || Selected is null) return;

        var group = Selected.Group;
        var newTypes = new List<SqlObjectType>();
        foreach (var s in Sections)
            if (s.IsScanEnabled) newTypes.AddRange(s.SqlTypes);

        // All-on collapses to null so the stored JSON stays tidy — the
        // record's EffectiveObjectTypes fallback restores "all" at runtime.
        IReadOnlyList<SqlObjectType>? persisted =
            newTypes.Count == WatchGroup.AllUserTypes.Count ? null : newTypes;

        var updated = group with { ObjectTypes = persisted };
        _svc.WatchGroups.Upsert(updated);
        _ = _svc.WatchGroups.SaveAsync();
        Selected.Replace(updated);

        if (!section.IsScanEnabled) section.Rows.Clear();
    }

    /// <summary>Called by MainWindow when the Watch tab is selected. Idempotent.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await _svc.WatchGroups.LoadAsync();
        RebuildGroups();
        foreach (var g in _svc.WatchGroups.All.Where(g => g.Enabled))
            StartGroupWatchers(g);
        Selected = Groups.FirstOrDefault();
        UpdateStatus();
    }

    /// <summary>
    /// Stops every running watcher in parallel, each capped at 3 seconds.
    /// Process-exit cleans up anything that refuses to stop in time.
    /// </summary>
    public Task ShutdownAsync()
    {
        var keys = _runs.Keys.ToList();
        if (keys.Count == 0) return Task.CompletedTask;
        return Task.WhenAll(keys.Select(k => StopOneAsync(k, TimeSpan.FromSeconds(3))));
    }

    // ---- Commands ----------------------------------------------------------

    [RelayCommand]
    private async Task NewGroupAsync()
    {
        var vm = await WatchGroupEditorViewModel.ShowAsync(_svc, null);
        if (vm?.Result is { } g)
        {
            _svc.WatchGroups.Upsert(g);
            await _svc.WatchGroups.SaveAsync();
            RebuildGroups();
            if (g.Enabled) StartGroupWatchers(g);
            Selected = Groups.FirstOrDefault(x => x.Group.Id == g.Id);
            UpdateStatus();
        }
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (Selected is null) return;
        var vm = await WatchGroupEditorViewModel.ShowAsync(_svc, Selected.Group);
        if (vm?.Result is { } g)
        {
            await StopGroupAsync(g.Id);
            _svc.WatchGroups.Upsert(g);
            await _svc.WatchGroups.SaveAsync();
            Selected?.Replace(g);
            if (g.Enabled) StartGroupWatchers(g);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is null) return;
        var id = Selected.Group.Id;
        await StopGroupAsync(id);
        _svc.WatchGroups.Remove(id);
        await _svc.WatchGroups.SaveAsync();
        Groups.Remove(Selected);
        Selected = Groups.FirstOrDefault();
        UpdateStatus();
    }

    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (Selected is null) return;
        var g = Selected.Group with { Enabled = !Selected.Group.Enabled };
        _svc.WatchGroups.Upsert(g);
        await _svc.WatchGroups.SaveAsync();
        // Flip UI state FIRST so the button label updates instantly.
        Selected.Replace(g);
        OnPropertyChanged(nameof(StartStopLabel));
        OnPropertyChanged(nameof(IsSelectedRunning));
        UpdateStatus();

        if (g.Enabled)
        {
            StartGroupWatchers(g);
        }
        else
        {
            // Fire-and-forget: UI already reflects "paused". The stop
            // itself happens in the background with a 3-second budget.
            _ = StopGroupAsync(g.Id, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Wipes the drift rows for the currently-selected group without
    /// stopping the watchers. Useful when a burst of stale results is
    /// cluttering the view — the next tick repopulates.
    /// </summary>
    [RelayCommand]
    private void ClearResults()
    {
        ClearSections();
        LastUpdatedText = "Cleared — waiting for next tick…";
    }

    [RelayCommand]
    private void SendToBatch()
    {
        if (Selected is null) { Status = "Select a watch group first."; return; }
        var changed = LiveRows.Where(r => r.IsSyncable).Select(r => r.ObjectName).Distinct().ToList();
        if (changed.Count == 0) { Status = "No syncable changes in the current view."; return; }
        var g = Selected.Group;
        // Primary target fed to Batch; Batch pane's own target-picker can add more.
        var tgtEnv = g.PrimaryTarget?.Environment ?? g.SourceEnv;
        SendToBatchRequested?.Invoke(changed, g.SourceEnv, tgtEnv, g.SourceDatabase);
    }

    // ---- DACPAC stage commands ---------------------------------------------

    [RelayCommand] private Task StageAllAsync()      => StageCoreAsync(_ => true, label: "all changes");
    [RelayCommand] private Task StageSelectedAsync() => StageCoreAsync(r => r.IsSelected, label: "selected rows");

    /// <summary>
    /// DACPAC-only flow shared by Stage All / Stage Selected. Exports the
    /// SOURCE definition of every row matching <paramref name="rowFilter"/>
    /// and, if enabled, stages on a new git branch. Never mutates targets.
    /// </summary>
    private async Task StageCoreAsync(Func<DriftRowVm, bool> rowFilter, string label)
    {
        if (Selected is null)
        {
            Status = "Select a watch group first.";
            _svc.Toasts.Warning("No watch group", "Pick a group on the left before staging.");
            return;
        }

        var exporter = await _svc.TryBuildDacpacExporterAsync();
        if (exporter is null)
        {
            Status = "DACPAC isn't configured. Open Settings → DACPAC and point it at your SSDT folder.";
            _svc.Toasts.Warning("DACPAC not configured",
                "Open Settings → DACPAC / SSDT export and point it at your SSDT folder first.");
            return;
        }

        var rowsToExport = LiveRows.Where(r => r.IsSyncable && rowFilter(r)).ToList();
        if (rowsToExport.Count == 0)
        {
            Status = $"No {label} to stage.";
            _svc.Toasts.Info("Nothing to stage", $"No {label} match the current filter.");
            return;
        }

        var g       = Selected.Group;
        var srcConn = _svc.Connections.Get(g.SourceEnv, g.SourceDatabase);
        if (string.IsNullOrWhiteSpace(srcConn))
        {
            Status = "No connection string for source.";
            _svc.Toasts.Error("No source connection", $"{g.SourceEnv}·{g.SourceDatabase} isn't configured.");
            return;
        }

        var exported = new List<string>();
        var newlyCreated = 0;
        foreach (var row in rowsToExport)
        {
            try
            {
                var id  = new ObjectIdentifier("dbo", row.ObjectName);
                // DACPAC path: pull the rich table script (constraints,
                // FKs, non-PK indexes, triggers) when writing into SSDT.
                var src = await _svc.Scripter.GetObjectForDacpacAsync(srcConn!, id);
                if (src is null) continue;

                var preExisting = exporter.RelativePathFor(src.Id, src.Type);
                var existedBefore = System.IO.File.Exists(
                    System.IO.Path.Combine(exporter.Options.RootFolder, preExisting));
                var path = exporter.Export(src.Id, src.Type, src.Definition);
                if (path is not null)
                {
                    exported.Add(path);
                    if (!existedBefore) newlyCreated++;
                }
            }
            catch { /* best-effort */ }
        }

        if (exported.Count == 0)
        {
            Status = "Nothing was written — check your DACPAC folder.";
            _svc.Toasts.Warning("Nothing written", "Check that the DACPAC folder exists and is writable.");
            return;
        }

        var updated = exported.Count - newlyCreated;
        var tally   = $"{updated} updated / {newlyCreated} new";

        Status = $"DACPAC export ({label}): {tally} — written to {exporter.Options.RootFolder}.";
        _svc.Toasts.Success($"DACPAC export ({label})", $"{tally} — written to {exporter.Options.RootFolder}");
    }

    // ---- Watcher lifecycle -------------------------------------------------

    /// <summary>Starts one watcher per target for the given group.</summary>
    private void StartGroupWatchers(WatchGroup group)
    {
        _groupAgg[group.Id] = new GroupAggregate();
        foreach (var t in group.Targets) StartOne(group, t);
    }

    private void StartOne(WatchGroup group, TargetRoute target)
    {
        var key = (group.Id, target.Key);
        if (_runs.ContainsKey(key)) return;

        // Capture only the id — BuildPlanAsync re-reads the group from the
        // store each tick so live edits (type toggles, object list changes)
        // take effect without restarting the watcher.
        var groupId = group.Id;
        var watcher = _svc.CreateWatcher(
            TimeSpan.FromSeconds(group.IntervalSeconds),
            ct => BuildPlanAsync(groupId, target, ct));

        var run = new WatcherRun(watcher, target);
        _runs[key] = run;
        watcher.Start();

        // IMPORTANT: batch ObjectDrifted events before dispatching to the
        // UI thread. Auto-discover groups can emit hundreds of drifts per
        // tick; one InvokeAsync per event floods the dispatcher and makes
        // Start/Stop feel frozen. We collect drifts into chunks and push
        // them as a single Post() — fire-and-forget, no awaits blocking
        // the consumer if the UI thread is busy.
        const int FlushEvery = 32;

        run.Consumer = Task.Run(async () =>
        {
            var pending = new List<ObjectDrifted>(FlushEvery);
            try
            {
                await foreach (var ev in watcher.Events.ReadAllAsync(run.Cts.Token))
                {
                    switch (ev)
                    {
                        case TickStarted:
                            pending.Clear();
                            Dispatcher.UIThread.Post(() => OnTickStarted(group.Id, target));
                            break;
                        case ObjectDrifted od:
                            pending.Add(od);
                            if (pending.Count >= FlushEvery)
                            {
                                var snapshot = pending.ToList();
                                pending.Clear();
                                Dispatcher.UIThread.Post(() => OnObjectsDrifted(group.Id, target, snapshot));
                            }
                            break;
                        case TickCompleted tc:
                            if (pending.Count > 0)
                            {
                                var snapshot = pending.ToList();
                                pending.Clear();
                                Dispatcher.UIThread.Post(() => OnObjectsDrifted(group.Id, target, snapshot));
                            }
                            run.LastBatch = tc.Batch;
                            Dispatcher.UIThread.Post(() => OnTickCompleted(group.Id, target, tc));
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// UI-thread handler that applies a chunk of <see cref="ObjectDrifted"/>
    /// events in one dispatcher entry. Reuses the single-event path so
    /// logic stays in exactly one place.
    /// </summary>
    private void OnObjectsDrifted(Guid groupId, TargetRoute target, List<ObjectDrifted> events)
    {
        foreach (var ev in events) OnObjectDrifted(groupId, target, ev);
    }

    /// <summary>
    /// Build the plan for one (groupId, target) pair. Auto-discovery happens
    /// on the source — the object list is shared across all targets, so
    /// each target watcher sees the same universe of objects. The group
    /// is looked up fresh from the store every tick so live edits pick up
    /// without restarting the watcher.
    /// </summary>
    private async Task<WatchPlan?> BuildPlanAsync(Guid groupId, TargetRoute target, CancellationToken ct)
    {
        var group = _svc.WatchGroups.Get(groupId);
        if (group is null) return null;
        var srcConn = _svc.Connections.Get(group.SourceEnv, group.SourceDatabase);
        var tgtConn = _svc.Connections.Get(target.Environment, target.Database);
        if (string.IsNullOrWhiteSpace(srcConn) || string.IsNullOrWhiteSpace(tgtConn)) return null;

        IReadOnlyCollection<ObjectIdentifier> ids;
        if (group.Objects.Count == 0)
        {
            // Auto-discover: narrow by the group's ObjectTypes filter so
            // users can scope a scan to (say) tables only and skip the cost
            // of fetching every proc definition. Empty filter list means
            // "all user types" — kept backward-compatible via the record's
            // EffectiveObjectTypes fallback.
            var refs = await _svc.Scripter.ListAllAsync(srcConn!, ct);
            var typeSet = new HashSet<SqlObjectType>(group.EffectiveObjectTypes);
            ids = refs.Where(r => typeSet.Contains(r.Type))
                      .Select(r => r.Id)
                      .ToArray();
        }
        else
        {
            // Explicit object list overrides the type filter — the user
            // picked these by name, so respect them verbatim.
            ids = group.Objects.Select(ObjectIdentifier.Parse).ToArray();
        }
        return new WatchPlan(srcConn!, tgtConn!, group.SourceEnv, target.Environment, ids);
    }

    // ---- Streaming event handlers (all on the UI thread) ------------------

    /// <summary>Mark every per-target state belonging to this target as "from the previous tick".</summary>
    private void OnTickStarted(Guid groupId, TargetRoute target)
    {
        if (Selected?.Group.Id != groupId) return;
        foreach (var s in Sections)
            foreach (var r in s.Rows)
                if (r.TargetStates.TryGetValue(target.Key, out var st)) st.SeenThisTick = false;
    }

    /// <summary>
    /// Upsert the per-target state for this object; filter InSync so rows
    /// only stay in view when they represent an actual difference somewhere.
    /// </summary>
    private void OnObjectDrifted(Guid groupId, TargetRoute target, ObjectDrifted ev)
    {
        var item = Groups.FirstOrDefault(g => g.Group.Id == groupId);
        if (item is null) return;

        var drift = ev.Drift;

        if (drift.IsSyncable || drift.Kind == DriftKind.Error)
            item.HasChanges = true;

        if (Selected?.Group.Id != groupId) return;

        // Locate or create the row across all sections — sections are
        // keyed by object type but membership is established on first sight.
        DriftRowVm? existing = null;
        WatchSectionVm? existingIn = null;
        foreach (var s in Sections)
        {
            var hit = s.Rows.FirstOrDefault(r => r.ObjectName == drift.Id.Name);
            if (hit is not null) { existing = hit; existingIn = s; break; }
        }

        var effectiveType = drift.SourceType != SqlObjectType.Unknown ? drift.SourceType : drift.TargetType;
        var targetSection = Sections.FirstOrDefault(s => s.Matches(effectiveType));

        // Guard against in-flight events for a type the user has since
        // toggled off. BuildPlanAsync already filters the next tick, but
        // events already queued from the previous tick would otherwise
        // repopulate a disabled section between the Rows.Clear() and the
        // tick boundary.
        if (targetSection is { IsScanEnabled: false }) return;

        // If this drift says "InSync" against this target, just update the
        // per-target state. Row stays if any OTHER target still differs;
        // otherwise it's pruned at TickCompleted.
        var state = new TargetDriftState
        {
            Environment  = target.Environment,
            Database     = target.Database,
            Kind         = drift.Kind,
            TargetHash   = Short(drift.TargetHash),
            Message      = drift.Message,
            SeenThisTick = true
        };

        if (existing is null)
        {
            if (drift.Kind == DriftKind.InSync) return;     // nothing to show yet
            if (targetSection is null) return;              // Unknown type bucket

            var row = new DriftRowVm
            {
                ObjectName = drift.Id.Name,
                ObjectType = effectiveType,
                SourceHash = Short(drift.SourceHash)
            };
            row.TargetStates[target.Key] = state;
            row.RebuildAggregate();
            targetSection.Rows.Add(row);
            return;
        }

        // Source hash is per-row; keep the latest non-empty value.
        if (!string.IsNullOrEmpty(drift.SourceHash))
            existing.SourceHash = Short(drift.SourceHash);

        existing.TargetStates[target.Key] = state;

        // Migrate section if the type changed (rare).
        if (targetSection is not null && existingIn != targetSection && existingIn is not null)
        {
            existingIn.Rows.Remove(existing);
            existing.ObjectType = effectiveType;
            targetSection.Rows.Add(existing);
        }

        existing.RebuildAggregate();

        // If every target now says InSync, drop the row.
        if (existing.IsAllInSync)
        {
            var section = Sections.FirstOrDefault(s => s.Rows.Contains(existing));
            section?.Rows.Remove(existing);
        }
    }

    /// <summary>
    /// Per-target TickCompleted: prune per-target states not seen this tick
    /// (object no longer in the plan) and recompute aggregates. A row with
    /// no remaining differing targets is removed.
    /// </summary>
    private void OnTickCompleted(Guid groupId, TargetRoute target, TickCompleted ev)
    {
        var item = Groups.FirstOrDefault(g => g.Group.Id == groupId);
        if (item is null) return;

        // Maintain per-group aggregate counters summed across targets.
        if (!_groupAgg.TryGetValue(groupId, out var agg))
        {
            agg = new GroupAggregate();
            _groupAgg[groupId] = agg;
        }
        agg.PerTarget[target.Key] = new TargetAggregate(ev.Changed, ev.Errors, ev.Total);

        var changed = agg.PerTarget.Values.Sum(v => v.Changed);
        var errors  = agg.PerTarget.Values.Sum(v => v.Errors);
        item.ChangedCount   = changed;
        item.ErrorCount     = errors;
        item.HasChanges     = changed > 0 || errors > 0;
        item.LastUpdatedUtc = ev.CapturedAt;

        if (Selected?.Group.Id == groupId)
        {
            // Prune stale per-target states, then drop rows that have no
            // differing targets left.
            foreach (var s in Sections)
            {
                for (int i = s.Rows.Count - 1; i >= 0; i--)
                {
                    var row = s.Rows[i];
                    if (row.TargetStates.TryGetValue(target.Key, out var st) && !st.SeenThisTick)
                        row.TargetStates.Remove(target.Key);

                    row.RebuildAggregate();
                    if (row.TargetStates.Count == 0 || row.IsAllInSync) s.Rows.RemoveAt(i);
                }
            }

            LastUpdatedText = BuildLastUpdatedText(ev.CapturedAt, agg);
        }
    }

    /// <summary>Stops every watcher associated with a group.</summary>
    private async Task StopGroupAsync(Guid groupId, TimeSpan? timeout = null)
    {
        var keys = _runs.Keys.Where(k => k.Group == groupId).ToList();
        if (keys.Count == 0) return;
        await Task.WhenAll(keys.Select(k => StopOneAsync(k, timeout)));
        _groupAgg.Remove(groupId);
    }

    /// <summary>Stops a single watcher. Non-blocking beyond <paramref name="timeout"/>.</summary>
    private async Task StopOneAsync((Guid Group, string Target) key, TimeSpan? timeout = null)
    {
        if (!_runs.Remove(key, out var run)) return;
        var budget = timeout ?? TimeSpan.FromSeconds(3);

        run.Cts.Cancel();
        var stopTask     = run.Watcher.StopAsync();
        var consumerTask = run.Consumer ?? Task.CompletedTask;
        var deadline     = Task.Delay(budget);
        try { await Task.WhenAny(Task.WhenAll(stopTask, consumerTask), deadline).ConfigureAwait(false); }
        catch { /* ignore */ }
        try { run.Cts.Dispose(); } catch { /* double-dispose is harmless */ }
    }

    // ---- Rendering ---------------------------------------------------------

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var g in _svc.WatchGroups.All.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            Groups.Add(new WatchGroupItemVm(g));
    }

    partial void OnSelectedChanging(WatchGroupItemVm? value)
    {
        // These labels depend on the selected group's state — flip them
        // on any selection change so the button never drifts out of sync.
        OnPropertyChanged(nameof(StartStopLabel));
        OnPropertyChanged(nameof(IsSelectedRunning));
    }

    partial void OnSelectedChanged(WatchGroupItemVm? value)
    {
        OnPropertyChanged(nameof(StartStopLabel));
        OnPropertyChanged(nameof(IsSelectedRunning));

        // Hydrate each section's scan toggle from the group's ObjectTypes.
        // EffectiveObjectTypes collapses an empty/null stored list into
        // "all user types" — so an untouched group shows every toggle on.
        // Suppress PropertyChanged so this hydration doesn't ping-pong
        // back into the store via OnSectionPropertyChanged.
        if (value is not null)
        {
            var effective = value.Group.EffectiveObjectTypes;
            foreach (var s in Sections)
            {
                s.PropertyChanged -= OnSectionPropertyChanged;
                s.IsScanEnabled = s.SqlTypes.Any(t => effective.Contains(t));
                s.PropertyChanged += OnSectionPropertyChanged;
            }
        }
        ClearSections();
        if (value is null)
        {
            HeaderTitle = ""; HeaderSubtitle = ""; LastUpdatedText = "";
            return;
        }
        var g = value.Group;
        HeaderTitle    = g.Name;
        var scope      = g.Objects.Count == 0 ? "all objects in database" : $"{g.Objects.Count} object(s)";
        var targetStr  = g.Targets.Count == 0
            ? "(no targets)"
            : string.Join(", ", g.Targets.Select(t => $"{t.Environment}·{t.Database}"));
        HeaderSubtitle = $"{g.SourceEnv}·{g.SourceDatabase}  →  {targetStr}  ·  {scope}  ·  every {g.IntervalSeconds}s  ·  {(g.Enabled ? "running" : "paused")}";

        // Repopulate rows from the freshest per-target batches we've seen.
        RenderFromLatestBatches(g.Id);
    }

    /// <summary>
    /// Replay every target's last-known batch into the rows. Called after
    /// a selection change so the pane doesn't look empty while waiting for
    /// the next tick.
    /// </summary>
    private void RenderFromLatestBatches(Guid groupId)
    {
        var runs = _runs.Where(r => r.Key.Group == groupId).ToList();
        if (runs.Count == 0) { LastUpdatedText = "Waiting for first tick…"; return; }

        DateTime mostRecent = DateTime.MinValue;
        foreach (var (key, run) in runs)
        {
            if (run.LastBatch is null) continue;
            if (run.LastBatch.CapturedAt > mostRecent) mostRecent = run.LastBatch.CapturedAt;

            foreach (var d in run.LastBatch.Items)
            {
                if (d.Kind == DriftKind.InSync) continue;
                var effType = d.SourceType != SqlObjectType.Unknown ? d.SourceType : d.TargetType;
                var section = Sections.FirstOrDefault(s => s.Matches(effType));
                if (section is null) continue;

                var row = section.Rows.FirstOrDefault(r => r.ObjectName == d.Id.Name);
                if (row is null)
                {
                    row = new DriftRowVm
                    {
                        ObjectName = d.Id.Name,
                        ObjectType = effType,
                        SourceHash = Short(d.SourceHash)
                    };
                    section.Rows.Add(row);
                }

                row.TargetStates[key.Target] = new TargetDriftState
                {
                    Environment  = run.Target.Environment,
                    Database     = run.Target.Database,
                    Kind         = d.Kind,
                    TargetHash   = Short(d.TargetHash),
                    Message      = d.Message,
                    SeenThisTick = true
                };
                row.RebuildAggregate();
            }
        }

        LastUpdatedText = mostRecent == DateTime.MinValue
            ? "Waiting for first tick…"
            : $"Last updated: {mostRecent.ToLocalTime():HH:mm:ss}";
    }

    private string BuildLastUpdatedText(DateTime at, GroupAggregate agg)
    {
        var changed = agg.PerTarget.Values.Sum(v => v.Changed);
        var errors  = agg.PerTarget.Values.Sum(v => v.Errors);
        var total   = agg.PerTarget.Values.Sum(v => v.Total);
        return $"Last updated: {at.ToLocalTime():HH:mm:ss}  ·  {changed} changed / {errors} errors / {total} total (across {agg.PerTarget.Count} target{(agg.PerTarget.Count == 1 ? "" : "s")})";
    }

    private void ClearSections()
    {
        foreach (var s in Sections) s.Rows.Clear();
    }

    private void UpdateStatus()
    {
        if (Groups.Count == 0) { Status = "No groups. Create one to start watching."; return; }
        int enabled = Groups.Count(g => g.Group.Enabled);
        int withChanges = Groups.Count(g => g.HasChanges);
        Status = $"{Groups.Count} group(s) · {enabled} running · {withChanges} with changes.";
    }

    private static string Short(string? h) => string.IsNullOrEmpty(h) ? "—" : h[..Math.Min(8, h.Length)];

    private sealed class WatcherRun
    {
        public ChangeWatcher Watcher { get; }
        public TargetRoute   Target  { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Consumer { get; set; }
        public DriftBatch? LastBatch;
        public WatcherRun(ChangeWatcher w, TargetRoute t) { Watcher = w; Target = t; }
    }

    private sealed record TargetAggregate(int Changed, int Errors, int Total);
    private sealed class GroupAggregate
    {
        public Dictionary<string, TargetAggregate> PerTarget { get; } = new();
    }
}
