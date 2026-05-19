using System.Collections.ObjectModel;
using System.ComponentModel;
using Base.It.App.Services;
using Base.It.Core.Models;
using Base.It.Core.Schema;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Row wrapper for the <see cref="SnapshotsViewModel.Entries"/> grid.
/// Lifts <see cref="SnapshotEntry"/> into VM space with a derived
/// human-readable kind label and a "size with KB suffix" for display.
/// </summary>
public sealed class SnapshotEntryVm
{
    public SnapshotEntry Source { get; }
    public string Schema   => Source.Schema;
    public string Name     => Source.Name;
    public string FullName => Source.FullName;
    public string Kind     => Source.Kind.ToString();
    public string Hash     => Source.Hash;
    public string HashShort => Source.Hash.Length > 12 ? Source.Hash[..12] + "…" : Source.Hash;
    public string SizeDisplay => Source.Size < 1024
        ? $"{Source.Size} B"
        : $"{Source.Size / 1024.0:N1} KB";

    /// <summary>True if this entry is a Table (used by the related-
    /// triggers panel to decide whether to render).</summary>
    public bool IsTable => Source.Kind == SqlObjectType.Table;

    /// <summary>Trigger's parent table — null on non-triggers and on
    /// legacy entries from snapshots taken before parent-tracking.</summary>
    public string? ParentSchema => Source.ParentSchema;
    public string? ParentName   => Source.ParentName;

    public SnapshotEntryVm(SnapshotEntry e) { Source = e; }
}

/// <summary>
/// VM wrapper around <see cref="SnapshotSummary"/> so the snapshot list
/// can support inline rename without polluting the Core record. Carries
/// an <see cref="IsEditing"/> flag (drives display vs. edit template
/// swap) and an <see cref="EditingName"/> buffer that the rename dialog
/// edits before commit.
/// </summary>
public sealed partial class SnapshotSummaryVm : ObservableObject
{
    public SnapshotSummary Source { get; private set; }

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editingName = "";

    public string  Id            => Source.Id;
    public DateTime TakenAtUtc   => Source.TakenAtUtc;
    public int     ObjectCount   => Source.ObjectCount;
    public long    TotalRawBytes => Source.TotalRawBytes;
    public string  FilePath      => Source.FilePath;
    public string? Name          => Source.Name;
    public string  DisplayName   => Source.DisplayName;
    public bool    HasCustomName => !string.IsNullOrWhiteSpace(Source.Name);

    public SnapshotSummaryVm(SnapshotSummary source)
    {
        Source = source;
        _editingName = source.Name ?? "";
    }

    /// <summary>Re-bind from a freshly-read summary (after a rename writes a new file).</summary>
    public void UpdateSource(SnapshotSummary updated)
    {
        Source = updated;
        EditingName = updated.Name ?? "";
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasCustomName));
    }
}

/// <summary>
/// One value in a per-column filter on the diff grid. The user ticks
/// values to include / unticks to hide. Sorting on enum-like columns
/// (Status, Object Type) doesn't carry useful meaning; per-column
/// filtering does — "show me only Changed StoredProcedures."
/// </summary>
public sealed partial class DiffFilterValue : ObservableObject
{
    [ObservableProperty] private bool _isIncluded = true;
    public string Value { get; }
    public DiffFilterValue(string value) { Value = value; }
}

/// <summary>
/// Row in the cross-store diff grid. <see cref="IsSelected"/> is bound
/// to the checkbox column so the user can tick exactly the changes
/// they want to promote. <see cref="ToggleSelectionCommand"/> exists
/// so the row's "checkbox" can be a styled Button (identical primitive
/// to the column header's select-all affordance) — Avalonia's real
/// CheckBox doesn't render in column headers, so this keeps the
/// header and row checkboxes visually aligned and consistent.
/// </summary>
public sealed partial class SnapshotDiffRowVm : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public string Status   { get; }
    public string Schema   { get; }
    public string Name     { get; }
    public string Kind     { get; }
    public string FromHash { get; }
    public string ToHash   { get; }

    public string FullName => $"{Schema}.{Name}";

    public SnapshotDiffRowVm(string status, SnapshotEntry? from, SnapshotEntry? to)
    {
        var anchor = from ?? to!;
        Status   = status;
        Schema   = anchor.Schema;
        Name     = anchor.Name;
        Kind     = anchor.Kind.ToString();
        FromHash = from?.Hash ?? "";
        ToHash   = to?.Hash   ?? "";
    }

    [RelayCommand]
    private void ToggleSelection() => IsSelected = !IsSelected;
}

public sealed partial class SnapshotsViewModel : ObservableObject
{
    private readonly AppServices _svc;

    // --- Endpoint browser (top) -------------------------------------

    public ObservableCollection<EndpointPick> Endpoints { get; } = new();

    [ObservableProperty] private EndpointPick? _selectedEndpoint;

    // --- Snapshot list + selected snapshot --------------------------

    public ObservableCollection<SnapshotSummaryVm> Snapshots { get; } = new();

    [ObservableProperty] private SnapshotSummaryVm? _selectedSnapshot;

    public ObservableCollection<SnapshotEntryVm> Entries { get; } = new();

    [ObservableProperty] private string _entryFilter = "";

    [ObservableProperty] private SnapshotEntryVm? _selectedEntry;

    [ObservableProperty] private string _entrySql = "";

    private List<SnapshotEntryVm> _allEntries = new();

    /// <summary>
    /// Triggers attached to the currently-selected table, by parent
    /// linkage captured at snapshot time (sys.triggers.parent_id).
    /// Empty when SelectedEntry isn't a table, or when this snapshot
    /// pre-dates parent-tracking and we don't have the linkage data.
    /// Drives the "Triggers on this table" sidebar in the SQL pane.
    /// </summary>
    public ObservableCollection<SnapshotEntryVm> RelatedTriggers { get; } = new();

    /// <summary>True when there's at least one related trigger to render.</summary>
    [ObservableProperty] private bool _hasRelatedTriggers;

    // ─── Entries grid: filters + sort state (matches compare grid UX) ───

    /// <summary>Distinct schema values in the current Entries set; user
    /// ticks/unticks values via the column's filter flyout.</summary>
    public ObservableCollection<DiffFilterValue> EntrySchemaFilterValues { get; } = new();

    /// <summary>Distinct object-type values in the current Entries set;
    /// user ticks/unticks via the column's filter flyout.</summary>
    public ObservableCollection<DiffFilterValue> EntryKindFilterValues   { get; } = new();

    [ObservableProperty] private NameSortDirection _entryNameSortMode = NameSortDirection.None;
    [ObservableProperty] private NameSortDirection _entrySizeSortMode = NameSortDirection.None;

    public string EntryNameSortIndicator => EntryNameSortMode switch
    {
        NameSortDirection.Asc  => "▲",
        NameSortDirection.Desc => "▼",
        _                       => "",
    };
    public string EntrySizeSortIndicator => EntrySizeSortMode switch
    {
        NameSortDirection.Asc  => "▲",
        NameSortDirection.Desc => "▼",
        _                       => "",
    };

    partial void OnEntryNameSortModeChanged(NameSortDirection value)
    {
        OnPropertyChanged(nameof(EntryNameSortIndicator));
        // Toggle one off when the other turns on so we don't try to
        // chain-sort by two keys at once — keeps the UX simple.
        if (value != NameSortDirection.None && EntrySizeSortMode != NameSortDirection.None)
            EntrySizeSortMode = NameSortDirection.None;
        ApplyEntryFilter();
    }
    partial void OnEntrySizeSortModeChanged(NameSortDirection value)
    {
        OnPropertyChanged(nameof(EntrySizeSortIndicator));
        if (value != NameSortDirection.None && EntryNameSortMode != NameSortDirection.None)
            EntryNameSortMode = NameSortDirection.None;
        ApplyEntryFilter();
    }

    [RelayCommand]
    private void ToggleEntryNameSort()
    {
        EntryNameSortMode = EntryNameSortMode switch
        {
            NameSortDirection.None => NameSortDirection.Asc,
            NameSortDirection.Asc  => NameSortDirection.Desc,
            _                       => NameSortDirection.None,
        };
    }

    [RelayCommand]
    private void ToggleEntrySizeSort()
    {
        EntrySizeSortMode = EntrySizeSortMode switch
        {
            NameSortDirection.None => NameSortDirection.Asc,
            NameSortDirection.Asc  => NameSortDirection.Desc,
            _                       => NameSortDirection.None,
        };
    }

    // --- Cross-store compare / promote ------------------------------

    [ObservableProperty] private EndpointPick? _diffFromEndpoint;
    [ObservableProperty] private EndpointPick? _diffToEndpoint;

    public ObservableCollection<SnapshotSummaryVm> DiffFromSnapshots { get; } = new();
    public ObservableCollection<SnapshotSummaryVm> DiffToSnapshots   { get; } = new();

    [ObservableProperty] private SnapshotSummaryVm? _diffFromSnapshot;
    [ObservableProperty] private SnapshotSummaryVm? _diffToSnapshot;

    public ObservableCollection<SnapshotDiffRowVm> DiffRows { get; } = new();

    /// <summary>
    /// Distinct status values (Added / Removed / Changed) present in
    /// the current diff, each with a tickable IsIncluded flag.
    /// Surfaced in the Status column header's filter flyout.
    /// </summary>
    public ObservableCollection<DiffFilterValue> StatusFilterValues { get; } = new();

    /// <summary>
    /// Distinct object-type values (StoredProcedure / View / Table /
    /// etc.) present in the current diff. Surfaced in the Object Type
    /// column header's filter flyout.
    /// </summary>
    public ObservableCollection<DiffFilterValue> KindFilterValues   { get; } = new();

    [ObservableProperty] private string  _diffSummary = "";
    [ObservableProperty] private bool    _diffHasResult;
    [ObservableProperty] private string  _diffFilter = "";

    [ObservableProperty] private int    _diffSelectedCount;
    [ObservableProperty] private bool   _hasDiffSelection;

    /// <summary>
    /// Tri-state binding for the diff grid's header checkbox.
    /// <c>true</c> = every visible row is ticked; <c>false</c> = none
    /// ticked; <c>null</c> = mixed (some). Drives <see cref="SelectAllGlyph"/>.
    /// </summary>
    [ObservableProperty] private bool? _allDiffRowsChecked = false;

    /// <summary>
    /// Glyph rendered inside the column-header "select all" affordance.
    /// Avalonia DataGrid silently drops a real <see cref="CheckBox"/> in
    /// column headers, so we use a Button styled like a checkbox and
    /// paint a tick / dash / nothing here to mirror the tri-state.
    /// </summary>
    public string SelectAllGlyph => AllDiffRowsChecked switch
    {
        true  => "✓",   // ✓
        null  => "–",   // –  (en-dash, reads as "mixed")
        _     => "",
    };

    /// <summary>
    /// Sort direction for the diff grid's Name column. Cycled by
    /// clicking the column header. <see cref="NameSortIndicator"/>
    /// paints the visual arrow next to the column text.
    /// </summary>
    public enum NameSortDirection { None, Asc, Desc }

    [ObservableProperty] private NameSortDirection _nameSortMode = NameSortDirection.None;

    public string NameSortIndicator => NameSortMode switch
    {
        NameSortDirection.Asc  => "▲",  // ▲
        NameSortDirection.Desc => "▼",  // ▼
        _                       => "",
    };

    private List<SnapshotDiffRowVm> _allDiffRows = new();

    /// <summary>
    /// Raised when the user clicks "Send to Batch" on the diff result.
    /// </summary>
    public event Action<SendToBatchPayload>? SendToBatchRequested;

    /// <summary>
    /// Fired right after a Compare run lands a fresh diff. The View
    /// listens and auto-scrolls the page so the result is visible
    /// without the user having to scroll manually.
    /// </summary>
    public event Action? DiffResultReady;

    // --- Stats + busy state -----------------------------------------

    [ObservableProperty] private int _statsSnapshotCount;
    [ObservableProperty] private int _statsUniqueObjects;
    [ObservableProperty] private string _statsDiskSize = "0 B";
    [ObservableProperty] private string _statsRawSize  = "0 B";
    [ObservableProperty] private string _statsSavings  = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Pick a connection and click Snapshot Now.";

    [ObservableProperty] private string _snapshotProgress = "";

    public string? CurrentStorePath { get; private set; }

    /// <summary>
    /// MinHeight for the snapshot-browser Grid. Snaps to 400 the moment a
    /// snapshot is picked (so the entries grid lands at full height, not
    /// growing-with-content as rows trickle in), and back to 0 when
    /// nothing's picked (so the section truly collapses to its title
    /// bar). The outer MaxHeight=400 in XAML still caps it at 400 either
    /// way, so this property only controls the floor.
    /// </summary>
    public double RightColumnMinHeight => SelectedSnapshot is null ? 0.0 : 400.0;

    public SnapshotsViewModel(AppServices svc)
    {
        _svc = svc;
        Reload();
    }

    public void Reload()
    {
        var keepKey   = SelectedEndpoint?.Key;
        var keepFromK = DiffFromEndpoint?.Key;
        var keepToK   = DiffToEndpoint?.Key;

        Endpoints.Clear();
        foreach (var ep in EnvironmentListProvider.Endpoints(_svc)) Endpoints.Add(ep);

        SelectedEndpoint = Endpoints.FirstOrDefault(e => e.Key == keepKey)
                        ?? Endpoints.FirstOrDefault();

        DiffFromEndpoint = Endpoints.FirstOrDefault(e => e.Key == keepFromK)
                       ?? SelectedEndpoint;
        DiffToEndpoint   = Endpoints.FirstOrDefault(e => e.Key == keepToK)
                       ?? SelectedEndpoint;
    }

    // ─── Browser side ───────────────────────────────────────────────

    partial void OnSelectedEndpointChanged(EndpointPick? value)
    {
        Snapshots.Clear();
        Entries.Clear();
        _allEntries.Clear();
        SelectedSnapshot = null;
        SelectedEntry    = null;
        EntrySql         = "";
        CurrentStorePath = null;
        OnPropertyChanged(nameof(CurrentStorePath));

        if (value is null) { ResetStatsDisplay(); return; }

        var store = _svc.OpenSchemaStore(value.Environment, value.Database);
        CurrentStorePath = store.Root;
        OnPropertyChanged(nameof(CurrentStorePath));

        LoadSnapshotsFromStore(store);
        RefreshStats(store);
        Status = Snapshots.Count == 0
            ? $"No snapshots yet for {value.Label}. Click Snapshot Now to capture the first one."
            : $"{Snapshots.Count} snapshot(s) available for {value.Label}.";
    }

    partial void OnSelectedSnapshotChanged(SnapshotSummaryVm? value)
    {
        _allEntries.Clear();
        Entries.Clear();
        SelectedEntry = null;
        EntrySql      = "";
        // Toggles the browser Grid's MinHeight between 0 and 400 so the
        // entries section snaps to full height on first click.
        OnPropertyChanged(nameof(RightColumnMinHeight));

        if (value is null || SelectedEndpoint is null) return;

        var store = _svc.OpenSchemaStore(SelectedEndpoint.Environment, SelectedEndpoint.Database);
        _ = LoadSnapshotEntriesAsync(store, value.Id);
    }

    partial void OnSelectedEntryChanged(SnapshotEntryVm? value)
    {
        RefreshRelatedTriggers(value);

        if (value is null || SelectedEndpoint is null) { EntrySql = ""; return; }
        var store = _svc.OpenSchemaStore(SelectedEndpoint.Environment, SelectedEndpoint.Database);
        _ = LoadEntrySqlAsync(store, value.Source.Hash);
    }

    /// <summary>
    /// Build the RelatedTriggers list from <see cref="_allEntries"/>: every
    /// Trigger whose ParentSchema/ParentName matches the currently-selected
    /// table. No-op for non-table selections — the list just clears.
    /// </summary>
    private void RefreshRelatedTriggers(SnapshotEntryVm? value)
    {
        RelatedTriggers.Clear();
        if (value is null || !value.IsTable)
        {
            HasRelatedTriggers = false;
            return;
        }

        foreach (var e in _allEntries)
        {
            if (e.Source.Kind != SqlObjectType.Trigger) continue;
            if (!string.Equals(e.ParentSchema, value.Schema, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(e.ParentName,   value.Name,   StringComparison.OrdinalIgnoreCase)) continue;
            RelatedTriggers.Add(e);
        }
        HasRelatedTriggers = RelatedTriggers.Count > 0;
    }

    /// <summary>
    /// Click handler for a row in the "Triggers on this table" sidebar.
    /// Switches <see cref="SelectedEntry"/> to the trigger so the SQL
    /// pane and the row highlight in the Entries grid both follow.
    /// </summary>
    [RelayCommand]
    private void JumpToTrigger(SnapshotEntryVm? trigger)
    {
        if (trigger is null) return;
        // Pick from the live Entries collection so the DataGrid highlights
        // the row visually — _allEntries items aren't necessarily the same
        // references after filter/sort rebuilds.
        var match = Entries.FirstOrDefault(e =>
            string.Equals(e.Schema, trigger.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Name,   trigger.Name,   StringComparison.OrdinalIgnoreCase));
        SelectedEntry = match ?? trigger;
    }

    partial void OnEntryFilterChanged(string value) => ApplyEntryFilter();

    private void ApplyEntryFilter()
    {
        Entries.Clear();
        var f = (EntryFilter ?? "").Trim();

        var allowedSchemas = EntrySchemaFilterValues
            .Where(v => v.IsIncluded)
            .Select(v => v.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedKinds = EntryKindFilterValues
            .Where(v => v.IsIncluded)
            .Select(v => v.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<SnapshotEntryVm> source = _allEntries;
        if (EntryNameSortMode == NameSortDirection.Asc)
            source = source.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        else if (EntryNameSortMode == NameSortDirection.Desc)
            source = source.OrderByDescending(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        else if (EntrySizeSortMode == NameSortDirection.Asc)
            source = source.OrderBy(e => e.Source.Size);
        else if (EntrySizeSortMode == NameSortDirection.Desc)
            source = source.OrderByDescending(e => e.Source.Size);

        foreach (var e in source)
        {
            if (EntrySchemaFilterValues.Count > 0 && !allowedSchemas.Contains(e.Schema)) continue;
            if (EntryKindFilterValues.Count   > 0 && !allowedKinds.Contains(e.Kind))     continue;
            if (!string.IsNullOrEmpty(f))
            {
                var matchText =
                       e.FullName.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || e.Kind.Contains(f, StringComparison.OrdinalIgnoreCase);
                if (!matchText) continue;
            }
            Entries.Add(e);
        }
    }

    /// <summary>
    /// Rebuild the distinct schema + kind values that show up in the
    /// Entries grid's column-header filter flyouts. Called once per
    /// snapshot load. All values default to ticked so the first view
    /// shows everything.
    /// </summary>
    private void RebuildEntryFilterValues()
    {
        foreach (var v in EntrySchemaFilterValues) v.PropertyChanged -= OnEntryFilterValueChanged;
        foreach (var v in EntryKindFilterValues)   v.PropertyChanged -= OnEntryFilterValueChanged;
        EntrySchemaFilterValues.Clear();
        EntryKindFilterValues.Clear();

        foreach (var s in _allEntries.Select(e => e.Schema)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var v = new DiffFilterValue(s);
            v.PropertyChanged += OnEntryFilterValueChanged;
            EntrySchemaFilterValues.Add(v);
        }
        foreach (var k in _allEntries.Select(e => e.Kind)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var v = new DiffFilterValue(k);
            v.PropertyChanged += OnEntryFilterValueChanged;
            EntryKindFilterValues.Add(v);
        }
    }

    private void OnEntryFilterValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffFilterValue.IsIncluded))
            ApplyEntryFilter();
    }

    private void LoadSnapshotsFromStore(SchemaStore store)
    {
        Snapshots.Clear();
        foreach (var s in store.ListSnapshots()) Snapshots.Add(new SnapshotSummaryVm(s));
        // Intentionally do NOT auto-select the newest snapshot here —
        // the entries grid stays empty until the user picks one,
        // matching the rest of the picker UX in the app.
        SelectedSnapshot = null;
    }

    private async Task LoadSnapshotEntriesAsync(SchemaStore store, string snapshotId)
    {
        try
        {
            var snap = await store.ReadSnapshotAsync(snapshotId);
            if (snap is null) return;
            _allEntries = snap.Entries
                .OrderBy(e => e.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name,    StringComparer.OrdinalIgnoreCase)
                .Select(e => new SnapshotEntryVm(e))
                .ToList();
            RebuildEntryFilterValues();
            ApplyEntryFilter();
        }
        catch (Exception ex)
        {
            _svc.Toasts.Error("Couldn't read snapshot", ex.Message);
        }
    }

    private async Task LoadEntrySqlAsync(SchemaStore store, string hash)
    {
        try
        {
            var sql = await store.ReadObjectAsync(hash);
            EntrySql = sql ?? "(object not in store)";
        }
        catch (Exception ex)
        {
            EntrySql = $"-- error reading object {hash}: {ex.Message}";
        }
    }

    // ─── Rename snapshot (inline edit in the list) ──────────────────

    /// <summary>
    /// Enter inline-edit mode for a snapshot row. Closes any other open
    /// editor first so only one is editable at a time. The view's
    /// pencil-icon click handler routes here.
    /// </summary>
    public void BeginRenameSnapshot(SnapshotSummaryVm vm)
    {
        if (vm is null) return;
        // Close any other open editor so only one is active at a time.
        foreach (var s in Snapshots) if (s != vm) s.IsEditing = false;
        vm.EditingName = vm.Name ?? "";
        vm.IsEditing = true;
    }

    /// <summary>
    /// Commit the inline-edit buffer. Persists via
    /// <see cref="SchemaStore.RenameSnapshotAsync"/> and re-reads the
    /// updated summary so every other view of the same snapshot picks
    /// up the new name. Empty / whitespace input clears the name.
    /// </summary>
    public async Task CommitRenameSnapshotAsync(SnapshotSummaryVm vm)
    {
        if (vm is null || !vm.IsEditing) return;
        if (SelectedEndpoint is null) { vm.IsEditing = false; return; }

        var store = _svc.OpenSchemaStore(SelectedEndpoint.Environment, SelectedEndpoint.Database);
        var newName = vm.EditingName?.Trim();
        try
        {
            await store.RenameSnapshotAsync(vm.Id, newName);
            var refreshed = store.ListSnapshots().FirstOrDefault(s => s.Id == vm.Id);
            if (refreshed is not null) vm.UpdateSource(refreshed);
            // Also propagate to compare-side dropdowns if the same snapshot is there.
            PropagateRenameToDiffLists(vm);
        }
        catch (Exception ex)
        {
            _svc.Toasts.Error("Rename failed", ex.Message);
        }
        finally
        {
            vm.IsEditing = false;
        }
    }

    /// <summary>Abandon the inline-edit buffer without persisting.</summary>
    public void CancelRenameSnapshot(SnapshotSummaryVm vm)
    {
        if (vm is null) return;
        vm.EditingName = vm.Name ?? "";
        vm.IsEditing = false;
    }

    /// <summary>
    /// If the renamed snapshot also appears in the cross-store compare
    /// dropdowns (same id), refresh its display there so the new name
    /// shows immediately.
    /// </summary>
    private void PropagateRenameToDiffLists(SnapshotSummaryVm renamed)
    {
        SnapshotSummaryVm? match;
        match = DiffFromSnapshots.FirstOrDefault(s => s.Id == renamed.Id);
        match?.UpdateSource(renamed.Source);
        match = DiffToSnapshots.FirstOrDefault(s => s.Id == renamed.Id);
        match?.UpdateSource(renamed.Source);
    }

    // ─── Cross-store compare side ───────────────────────────────────

    partial void OnDiffFromEndpointChanged(EndpointPick? value)
        => ReloadDiffSnapshotsFor(value, DiffFromSnapshots, s => DiffFromSnapshot = s);

    partial void OnDiffToEndpointChanged(EndpointPick? value)
        => ReloadDiffSnapshotsFor(value, DiffToSnapshots,   s => DiffToSnapshot   = s);

    private void ReloadDiffSnapshotsFor(
        EndpointPick? endpoint,
        ObservableCollection<SnapshotSummaryVm> list,
        Action<SnapshotSummaryVm?> setSelected)
    {
        list.Clear();
        if (endpoint is null) { setSelected(null); return; }
        var store = _svc.OpenSchemaStore(endpoint.Environment, endpoint.Database);
        foreach (var s in store.ListSnapshots()) list.Add(new SnapshotSummaryVm(s));
        setSelected(list.FirstOrDefault());
    }

    partial void OnDiffFilterChanged(string value) => ApplyDiffFilter();

    private void ApplyDiffFilter()
    {
        DiffRows.Clear();
        var f = (DiffFilter ?? "").Trim();

        // Build sets of currently-allowed status / kind values. Empty
        // set is treated as "no value passes" — matches user intent
        // when they untick everything in a column filter.
        var allowedStatuses = StatusFilterValues
            .Where(v => v.IsIncluded)
            .Select(v => v.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedKinds = KindFilterValues
            .Where(v => v.IsIncluded)
            .Select(v => v.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Honor the Name column sort. None preserves natural insertion
        // order (Changed → Added → Removed). Asc/Desc sort by FullName.
        IEnumerable<SnapshotDiffRowVm> source = NameSortMode switch
        {
            NameSortDirection.Asc  => _allDiffRows.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            NameSortDirection.Desc => _allDiffRows.OrderByDescending(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            _                       => _allDiffRows,
        };

        foreach (var r in source)
        {
            if (StatusFilterValues.Count > 0 && !allowedStatuses.Contains(r.Status)) continue;
            if (KindFilterValues.Count   > 0 && !allowedKinds.Contains(r.Kind))      continue;
            if (!string.IsNullOrEmpty(f))
            {
                var matchText =
                       r.FullName.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || r.Kind.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || r.Status.Contains(f, StringComparison.OrdinalIgnoreCase);
                if (!matchText) continue;
            }
            DiffRows.Add(r);
        }
        RefreshHeaderCheckState();
    }

    partial void OnNameSortModeChanged(NameSortDirection value)
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        ApplyDiffFilter();
    }

    /// <summary>
    /// Click handler for the Name column header. Cycles through
    /// no-sort → ascending → descending → no-sort. The arrow next to
    /// "Name" updates via <see cref="NameSortIndicator"/>.
    /// </summary>
    [RelayCommand]
    private void ToggleNameSort()
    {
        NameSortMode = NameSortMode switch
        {
            NameSortDirection.None => NameSortDirection.Asc,
            NameSortDirection.Asc  => NameSortDirection.Desc,
            _                       => NameSortDirection.None,
        };
    }

    /// <summary>
    /// Click handler for the column-header "select all" affordance.
    /// If any visible row is selected, the click clears selections;
    /// otherwise it selects every visible row. This is the same
    /// behavior a tri-state checkbox would produce — Avalonia just
    /// won't render a real CheckBox in a DataGrid column header.
    /// </summary>
    [RelayCommand]
    private void ToggleAllDiffRows()
    {
        if (DiffRows.Count == 0) return;
        var anySelected = DiffRows.Any(r => r.IsSelected);
        foreach (var r in DiffRows) r.IsSelected = !anySelected;
    }

    /// <summary>
    /// Rebuild <see cref="StatusFilterValues"/> + <see cref="KindFilterValues"/>
    /// from the distinct status / kind values present in
    /// <see cref="_allDiffRows"/>. Called once per Compare run. All
    /// values default to ticked so the first view shows everything.
    /// </summary>
    private void RebuildColumnFilterValues()
    {
        // Detach property-changed listeners on the old set.
        foreach (var v in StatusFilterValues) v.PropertyChanged -= OnFilterValueChanged;
        foreach (var v in KindFilterValues)   v.PropertyChanged -= OnFilterValueChanged;
        StatusFilterValues.Clear();
        KindFilterValues.Clear();

        foreach (var s in _allDiffRows.Select(r => r.Status)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var v = new DiffFilterValue(s);
            v.PropertyChanged += OnFilterValueChanged;
            StatusFilterValues.Add(v);
        }
        foreach (var k in _allDiffRows.Select(r => r.Kind)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var v = new DiffFilterValue(k);
            v.PropertyChanged += OnFilterValueChanged;
            KindFilterValues.Add(v);
        }
    }

    private void OnFilterValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffFilterValue.IsIncluded))
            ApplyDiffFilter();
    }

    private void OnDiffRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SnapshotDiffRowVm.IsSelected))
        {
            RefreshDiffSelectionCount();
            RefreshHeaderCheckState();
        }
    }

    private void RefreshDiffSelectionCount()
    {
        DiffSelectedCount = _allDiffRows.Count(r => r.IsSelected);
        HasDiffSelection  = DiffSelectedCount > 0;
    }

    /// <summary>
    /// Compute the tri-state header checkbox value from the current
    /// visible rows. Suppresses the echo so we don't recursively fire
    /// <see cref="OnAllDiffRowsCheckedChanged"/> when only the header
    /// is supposed to reflect underlying row state.
    /// </summary>
    private void RefreshHeaderCheckState()
    {
        var total = DiffRows.Count;
        bool? next;
        if (total == 0) next = false;
        else
        {
            var c = DiffRows.Count(r => r.IsSelected);
            next = c == 0 ? false : c == total ? true : (bool?)null;
        }
        if (AllDiffRowsChecked != next) AllDiffRowsChecked = next;
    }

    /// <summary>
    /// Mirrors the current select-state into the column-header glyph.
    /// We no longer drive row selection from here — the header button's
    /// <see cref="ToggleAllDiffRowsCommand"/> does that directly — so
    /// this is purely a one-way reflection of row state into the glyph.
    /// </summary>
    partial void OnAllDiffRowsCheckedChanged(bool? value)
    {
        OnPropertyChanged(nameof(SelectAllGlyph));
    }

    [RelayCommand]
    private async Task SnapshotNowAsync()
    {
        var ep = SelectedEndpoint;
        if (ep is null)
        {
            _svc.Toasts.Warning("Pick a connection", "Select an endpoint before snapshotting.");
            return;
        }

        var conn = _svc.Connections.Get(ep.Environment, ep.Database);
        if (string.IsNullOrWhiteSpace(conn))
        {
            _svc.Toasts.Error("No connection string", $"{ep.Environment}·{ep.Database} isn't configured.");
            return;
        }

        IsBusy = true;
        SnapshotProgress = "Phase 1/2: querying server catalog…";
        Status = $"Snapshotting {ep.Label}…";

        try
        {
            var store = _svc.OpenSchemaStore(ep.Environment, ep.Database);
            var snapshotter = new Base.It.Core.Schema.SchemaSnapshotter(store);

            int statsRefreshing = 0;
            using var statsTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref statsRefreshing, 1, 0) != 0) return;
                try
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try { RefreshStats(store); }
                        finally { Interlocked.Exchange(ref statsRefreshing, 0); }
                    });
                }
                catch { Interlocked.Exchange(ref statsRefreshing, 0); }
            }, null, dueTime: 1000, period: 1000);

            var progress = new Progress<Base.It.Core.Schema.SnapshotProgress>(p =>
            {
                var reusedHint = p.ReusedFromPrevious > 0
                    ? $" · reusing {p.ReusedFromPrevious:N0} from previous"
                    : "";
                SnapshotProgress = p.Phase switch
                {
                    Base.It.Core.Schema.SnapshotPhase.Fetching => p.Done == 0 && p.Total == 0
                        ? $"Phase 1/2: querying server catalog…{reusedHint} ({p.FetchTime.TotalSeconds:N1}s)"
                        : p.Total == 0
                            ? $"Phase 1/2: fetched {p.Done:N0} objects so far…{reusedHint} ({p.FetchTime.TotalSeconds:N1}s)"
                            : $"Phase 1/2: fetched {p.Done:N0} / {p.Total:N0} changed objects{reusedHint} ({p.FetchTime.TotalSeconds:N1}s)",
                    Base.It.Core.Schema.SnapshotPhase.Writing  => $"Phase 2/2: writing · {p.Done:N0} / {p.Total:N0}{reusedHint} · fetch took {p.FetchTime.TotalSeconds:N1}s",
                    _                                          => $"Done · {p.Total:N0} objects"
                };
            });

            var result = await Task.Run(() =>
                snapshotter.SnapshotAsync(conn!, ep.Environment, ep.Database, progress));

            LoadSnapshotsFromStore(store);
            RefreshStats(store);
            SelectedSnapshot = Snapshots.FirstOrDefault(s => s.Id == result.Snapshot.Id);
            RefreshDiffSnapshotsIfTouched(ep);

            var t = result.Timing;
            var modeLabel = t.WasIncremental
                ? $"incremental — reused {t.ReusedCount:N0}, fetched {t.FetchedCount:N0}"
                : "full snapshot";
            var compLabel = t.UsedCompression ? "compression: yes" : "compression: NO";
            var connLabel = $"{t.Connections} parallel conn";

            Status = $"Snapshotted {result.Snapshot.Entries.Count:N0} object(s) in {t.Total.TotalSeconds:N1}s "
                   + $"({modeLabel}, {compLabel}, {connLabel}). "
                   + $"Fetch {t.Fetch.TotalSeconds:N1}s · write {t.Write.TotalSeconds:N1}s · pointer {t.Pointer.TotalMilliseconds:N0}ms. "
                   + $"Snapshot id {result.Snapshot.Id}.";
            _svc.Toasts.Success("Snapshot captured",
                $"{result.Snapshot.Entries.Count:N0} obj · {t.Total.TotalSeconds:N1}s · {modeLabel}");
        }
        catch (Exception ex)
        {
            Status = $"Snapshot failed: {ex.Message}";
            _svc.Toasts.Error("Snapshot failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            SnapshotProgress = "";
        }
    }

    private void RefreshDiffSnapshotsIfTouched(EndpointPick touched)
    {
        if (DiffFromEndpoint?.Key == touched.Key)
        {
            var keep = DiffFromSnapshot?.Id;
            var store = _svc.OpenSchemaStore(touched.Environment, touched.Database);
            DiffFromSnapshots.Clear();
            foreach (var s in store.ListSnapshots()) DiffFromSnapshots.Add(new SnapshotSummaryVm(s));
            DiffFromSnapshot = DiffFromSnapshots.FirstOrDefault(s => s.Id == keep)
                            ?? DiffFromSnapshots.FirstOrDefault();
        }
        if (DiffToEndpoint?.Key == touched.Key)
        {
            var keep = DiffToSnapshot?.Id;
            var store = _svc.OpenSchemaStore(touched.Environment, touched.Database);
            DiffToSnapshots.Clear();
            foreach (var s in store.ListSnapshots()) DiffToSnapshots.Add(new SnapshotSummaryVm(s));
            DiffToSnapshot = DiffToSnapshots.FirstOrDefault(s => s.Id == keep)
                          ?? DiffToSnapshots.FirstOrDefault();
        }
    }

    [RelayCommand]
    private async Task CompareSnapshotsAsync()
    {
        var fromEp = DiffFromEndpoint;
        var toEp   = DiffToEndpoint;
        if (fromEp is null || toEp is null || DiffFromSnapshot is null || DiffToSnapshot is null)
        {
            _svc.Toasts.Warning("Pick both sides", "Set From and To (endpoint + snapshot) before comparing.");
            return;
        }
        if (fromEp.Key == toEp.Key && string.Equals(DiffFromSnapshot.Id, DiffToSnapshot.Id))
        {
            _svc.Toasts.Info("Same snapshot", "Pick two different snapshots.");
            return;
        }

        IsBusy = true;
        try
        {
            var fromStore = _svc.OpenSchemaStore(fromEp.Environment, fromEp.Database);
            var toStore   = _svc.OpenSchemaStore(toEp.Environment,   toEp.Database);

            var from = await Task.Run(() => fromStore.ReadSnapshotAsync(DiffFromSnapshot.Id));
            var to   = await Task.Run(() => toStore.ReadSnapshotAsync(DiffToSnapshot.Id));
            if (from is null || to is null)
            {
                _svc.Toasts.Error("Couldn't read snapshots", "One of the picked snapshots is missing.");
                return;
            }

            var diff = SchemaStore.Diff(from, to);

            foreach (var r in _allDiffRows) r.PropertyChanged -= OnDiffRowPropertyChanged;

            _allDiffRows = new List<SnapshotDiffRowVm>(diff.Added.Count + diff.Removed.Count + diff.Changed.Count);
            foreach (var c in diff.Changed) _allDiffRows.Add(new SnapshotDiffRowVm("Changed", c.From, c.To));
            foreach (var a in diff.Added)   _allDiffRows.Add(new SnapshotDiffRowVm("Added",   null,   a));
            foreach (var r in diff.Removed) _allDiffRows.Add(new SnapshotDiffRowVm("Removed", r,      null));
            foreach (var r in _allDiffRows) r.PropertyChanged += OnDiffRowPropertyChanged;

            DiffSummary   = $"{diff.Added.Count} added · {diff.Removed.Count} removed · {diff.Changed.Count} changed";
            DiffHasResult = true;
            RebuildColumnFilterValues();
            ApplyDiffFilter();
            RefreshDiffSelectionCount();
            Status = $"Compared {fromEp.Label} ({DiffFromSnapshot.DisplayName}) ↔ {toEp.Label} ({DiffToSnapshot.DisplayName}): {DiffSummary}.";
            // Let the view bring the freshly-loaded result into view —
            // saves the user a scroll on every Compare click.
            DiffResultReady?.Invoke();
        }
        catch (Exception ex)
        {
            _svc.Toasts.Error("Diff failed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Clear the diff result entirely — removes every row from the
    /// list (not just unticks them). The Compare button has to be
    /// re-clicked to populate the list again. Matches the user's
    /// expectation of "Clear" meaning "empty this view."
    /// </summary>
    [RelayCommand]
    private void ClearDiffList()
    {
        foreach (var r in _allDiffRows) r.PropertyChanged -= OnDiffRowPropertyChanged;
        foreach (var v in StatusFilterValues) v.PropertyChanged -= OnFilterValueChanged;
        foreach (var v in KindFilterValues)   v.PropertyChanged -= OnFilterValueChanged;
        _allDiffRows = new List<SnapshotDiffRowVm>();
        DiffRows.Clear();
        StatusFilterValues.Clear();
        KindFilterValues.Clear();
        DiffSummary   = "";
        DiffHasResult = false;
        DiffSelectedCount = 0;
        HasDiffSelection  = false;
        AllDiffRowsChecked = false;
    }

    /// <summary>
    /// Send the ticked diff rows to the Batch screen for execution.
    /// The Watch→Batch handoff dialog handles "main has rows / new
    /// window / replace."
    /// </summary>
    [RelayCommand]
    private void SendDiffToBatch()
    {
        var from = DiffFromEndpoint;
        var to   = DiffToEndpoint;
        if (from is null || to is null)
        {
            _svc.Toasts.Warning("Pick both endpoints", "Set From and To before sending.");
            return;
        }
        var selected = _allDiffRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _svc.Toasts.Warning("Nothing ticked", "Tick rows in the diff first.");
            return;
        }

        var names = selected.Select(r => r.FullName).ToList();
        var payload = new SendToBatchPayload(
            ObjectNames:    names,
            SourceEnv:      from.Environment,
            SourceDatabase: from.Database,
            Targets:        new[] { (Environment: to.Environment, Database: to.Database) });

        SendToBatchRequested?.Invoke(payload);
    }

    /// <summary>
    /// Build a side-by-side preview for one diff row. Reads the FROM
    /// and TO SQL from their respective snapshot stores (different
    /// stores when the diff spans environments) and hands them to the
    /// shared preview window with red-line diff alignment applied.
    /// Returns null if either side's SQL can't be read.
    /// </summary>
    public async Task<BatchPreviewViewModel?> BuildDiffPreviewAsync(SnapshotDiffRowVm row)
    {
        if (row is null) return null;
        var fromEp = DiffFromEndpoint;
        var toEp   = DiffToEndpoint;
        if (fromEp is null || toEp is null) return null;

        var fromStore = _svc.OpenSchemaStore(fromEp.Environment, fromEp.Database);
        var toStore   = _svc.OpenSchemaStore(toEp.Environment,   toEp.Database);

        // For Added rows there's no FROM hash; for Removed, no TO. Use
        // an explanatory placeholder on the missing side so the user can
        // still see what's on the present side, alongside a one-liner
        // explaining why the other pane is empty.
        var fromSql = !string.IsNullOrEmpty(row.FromHash)
            ? await fromStore.ReadObjectAsync(row.FromHash) ?? "-- (definition missing from store)"
            : "-- (object did not exist in the FROM snapshot)";
        var toSql = !string.IsNullOrEmpty(row.ToHash)
            ? await toStore.ReadObjectAsync(row.ToHash) ?? "-- (definition missing from store)"
            : "-- (object did not exist in the TO snapshot)";

        return BatchPreviewViewModel.ForLiteralPair(
            svc:             _svc,
            title:           $"Preview: {row.FullName} · {row.Status}",
            leftLabel:       $"From · {fromEp.Label}",
            leftColor:       fromEp.Color,
            leftDefinition:  fromSql,
            rightLabel:      $"To · {toEp.Label}",
            rightColor:      toEp.Color,
            rightDefinition: toSql);
    }

    [RelayCommand]
    private void OpenStoreFolder()
    {
        if (string.IsNullOrWhiteSpace(CurrentStorePath))
        {
            _svc.Toasts.Warning("No store yet", "Pick an endpoint first.");
            return;
        }
        try
        {
            if (!Directory.Exists(CurrentStorePath)) Directory.CreateDirectory(CurrentStorePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = CurrentStorePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _svc.Toasts.Error("Open failed", ex.Message);
        }
    }

    private void RefreshStats(SchemaStore store)
    {
        var s = store.GetStats();
        StatsSnapshotCount = s.SnapshotCount;
        StatsUniqueObjects = s.UniqueObjectCount;
        StatsDiskSize = FormatBytes(s.ObjectsDiskBytes);
        StatsRawSize  = FormatBytes(s.ObjectsRawBytes);
        if (s.ObjectsRawBytes > 0)
        {
            var savedPercent = (1.0 - s.CompressionRatio) * 100.0;
            StatsSavings = $"saved ~{savedPercent:N0}%";
        }
        else
        {
            StatsSavings = "";
        }
    }

    private void ResetStatsDisplay()
    {
        StatsSnapshotCount = 0;
        StatsUniqueObjects = 0;
        StatsDiskSize = "0 B";
        StatsRawSize  = "0 B";
        StatsSavings  = "";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)            return $"{bytes} B";
        if (bytes < 1024 * 1024)     return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):N1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):N2} GB";
    }

    // ApplyFind / CurrentFindText removed: Ctrl+F is no longer wired to
    // the Entries-grid filter. The grid has its own filter textbox; the
    // two should stay separate (OS-standard Ctrl+F doesn't drive a list
    // filter elsewhere in the app either).
}
