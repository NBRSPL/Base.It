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
/// The relationship of one object between the SOURCE and the whole set of ticked
/// TARGETS — every meaningful permutation, collapsed to a single badge (with the
/// exact per-target breakdown in the row tooltip). Ordering also drives the
/// "Sync" column sort: most-actionable first, settled/absent last.
/// </summary>
public enum BatchSyncState
{
    /// <summary>Not resolved yet (no source/targets picked, or the check is still running).</summary>
    Unknown = 0,
    /// <summary>Present in the source and present-and-differing in EVERY target → will ALTER all.</summary>
    OutOfSync,
    /// <summary>Present in the source, absent from EVERY target → will CREATE on all.</summary>
    WillCreate,
    /// <summary>A mix across targets — some in sync, some differ, some missing. Tooltip has the counts.</summary>
    Partial,
    /// <summary>Present and identical in the source and EVERY target → nothing to do.</summary>
    InSync,
    /// <summary>Missing from the source but present in one or more targets → nothing to push.</summary>
    NotInSource,
    /// <summary>Not found in the source OR any target — the name doesn't resolve anywhere.</summary>
    NotAnywhere,
}

/// <summary>Glyphs + labels for <see cref="BatchSyncState"/>. The label carries the
/// glyph too, so the column's filter flyout doubles as the legend. One place so the
/// badge, the tooltip, and the filter all agree.</summary>
internal static class BatchSyncStateDisplay
{
    public static string Glyph(BatchSyncState s) => s switch
    {
        BatchSyncState.InSync      => "✓",
        BatchSyncState.WillCreate  => "＋",
        BatchSyncState.OutOfSync   => "≠",
        BatchSyncState.Partial     => "◐",
        BatchSyncState.NotInSource => "⊘",
        BatchSyncState.NotAnywhere => "✕",
        _                          => "",
    };

    /// <summary>Legend/facet label — glyph + words (blank for Unknown so it isn't a filter facet).</summary>
    public static string Label(BatchSyncState s) => s switch
    {
        BatchSyncState.InSync      => "✓  In sync",
        BatchSyncState.WillCreate  => "＋  New (will create)",
        BatchSyncState.OutOfSync   => "≠  Out of sync",
        BatchSyncState.Partial     => "◐  Partial / mixed",
        BatchSyncState.NotInSource => "⊘  Not in source",
        BatchSyncState.NotAnywhere => "✕  Not found anywhere",
        _                          => "",
    };
}

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

/// <summary>Short, readable labels for <see cref="Base.It.Core.Models.SqlObjectType"/>
/// used by the Batch Type column + its facet filter. One place so the column and
/// the filter values always agree.</summary>
internal static class SqlObjectTypeDisplay
{
    public static string Label(Base.It.Core.Models.SqlObjectType t) => t switch
    {
        Base.It.Core.Models.SqlObjectType.Table               => "Table",
        Base.It.Core.Models.SqlObjectType.View                => "View",
        Base.It.Core.Models.SqlObjectType.StoredProcedure     => "Procedure",
        Base.It.Core.Models.SqlObjectType.ScalarFunction      => "Scalar function",
        Base.It.Core.Models.SqlObjectType.InlineTableFunction => "Inline TVF",
        Base.It.Core.Models.SqlObjectType.TableValuedFunction => "Table function",
        Base.It.Core.Models.SqlObjectType.Trigger             => "Trigger",
        Base.It.Core.Models.SqlObjectType.TableType           => "Table type",
        Base.It.Core.Models.SqlObjectType.UserDefinedDataType => "UDDT",
        _                                                     => "",
    };
}

public sealed partial class BatchItem : ObservableObject
{
    [ObservableProperty] private bool        _isSelected;
    [ObservableProperty] private int         _index;
    [ObservableProperty] private string      _name    = "";
    [ObservableProperty] private BatchStatus _status  = BatchStatus.Pending;
    [ObservableProperty] private string      _message = "";

    /// <summary>
    /// The full source→targets relationship for this object (see
    /// <see cref="BatchSyncState"/>). Single source of truth for the badge, the
    /// column filter, the sort rank, and the derived <see cref="IsInSync"/> /
    /// <see cref="WillCreate"/> shims below. Set by
    /// <see cref="BatchViewModel.RunSyncChecksAsync"/> on the UI thread.
    /// </summary>
    [ObservableProperty] private BatchSyncState _state = BatchSyncState.Unknown;

    /// <summary>Human-readable per-target breakdown, shown as the badge's tooltip
    /// (e.g. "2 in sync · 1 differs · 1 will create").</summary>
    [ObservableProperty] private string _syncCheckHint = "";

    /// <summary>
    /// The object's catalog type (Table / View / Procedure / Function / …),
    /// resolved by the fast metadata pass (one <c>ListAllAsync</c> query on the
    /// source, not a per-object fetch) so it appears almost immediately after a
    /// paste. Null until resolved (or if the source doesn't have the object).
    /// </summary>
    [ObservableProperty] private Base.It.Core.Models.SqlObjectType? _objectType;

    /// <summary>Human label for <see cref="ObjectType"/> — drives the Type column
    /// and its facet filter. Blank while unresolved.</summary>
    public string TypeLabel => ObjectType is { } t ? SqlObjectTypeDisplay.Label(t) : "";

    partial void OnObjectTypeChanged(Base.It.Core.Models.SqlObjectType? value)
        => OnPropertyChanged(nameof(TypeLabel));

    // ── State-derived display + back-compat shims ──────────────────────────
    // Everything below is computed from State so there's one source of truth.
    // IsInSync / WillCreate keep their old meaning so the Hide-in-sync filter and
    // any other consumer don't need to change.

    /// <summary>Tri-state kept for the Hide-in-sync filter: true only when fully in
    /// sync, false for any actionable difference, null while unknown / not pushable.</summary>
    public bool? IsInSync => State switch
    {
        BatchSyncState.InSync                                   => true,
        BatchSyncState.Unknown or BatchSyncState.NotInSource
            or BatchSyncState.NotAnywhere                       => (bool?)null,
        _                                                       => false,
    };

    /// <summary>True when Execute will CREATE this on every target.</summary>
    public bool WillCreate => State == BatchSyncState.WillCreate;

    /// <summary>Glyph label for the column's facet filter + legend (blank while unknown).</summary>
    public string StateLabel => BatchSyncStateDisplay.Label(State);

    /// <summary>The single badge glyph shown in the state column.</summary>
    public string StateGlyph => BatchSyncStateDisplay.Glyph(State);

    // Per-state visibility shims — one glyph is themed + shown at a time in XAML.
    public bool ShowInSyncBadge      => State == BatchSyncState.InSync;
    public bool ShowCreateBadge      => State == BatchSyncState.WillCreate;
    public bool ShowOutOfSyncBadge   => State == BatchSyncState.OutOfSync;
    public bool ShowPartialBadge     => State == BatchSyncState.Partial;
    public bool ShowNotInSourceBadge => State == BatchSyncState.NotInSource;
    public bool ShowNotAnywhereBadge => State == BatchSyncState.NotAnywhere;

    partial void OnStateChanged(BatchSyncState value)
    {
        OnPropertyChanged(nameof(IsInSync));
        OnPropertyChanged(nameof(WillCreate));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(ShowInSyncBadge));
        OnPropertyChanged(nameof(ShowCreateBadge));
        OnPropertyChanged(nameof(ShowOutOfSyncBadge));
        OnPropertyChanged(nameof(ShowPartialBadge));
        OnPropertyChanged(nameof(ShowNotInSourceBadge));
        OnPropertyChanged(nameof(ShowNotAnywhereBadge));
    }

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
public sealed partial class BatchViewModel : ObservableObject, ICsvExportable
{
    private readonly AppServices _svc;

    /// <summary>Click-to-sort state for the items grid (Object name / Status).</summary>
    public ColumnSorter Sorter { get; } = new();

    /// <summary>Exposed so the View's Export handler can fire the result toast.</summary>
    public ToastService Toasts => _svc.Toasts;

    [ObservableProperty] private string? _sourceEnv;
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _statusFilter = "All";
    /// <summary>Name-substring filter, intersects with <see cref="StatusFilter"/>.</summary>
    [ObservableProperty] private string _nameFilter = "";
    /// <summary>
    /// When true, rows whose source + all ticked targets already match
    /// (<see cref="BatchItem.IsInSync"/> == true) are hidden from the grid
    /// so the operator can focus on — and execute — only what actually
    /// needs changing. In-sync detection is content-hash based, so with
    /// the formatter-aware hash a formatting-only difference no longer
    /// keeps a row visible. Intersects with the status + name filters.
    /// </summary>
    [ObservableProperty] private bool _hideInSync;
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
    // set, the user's label becomes the folder's trailing stamp (e.g.
    // "source_DEV_before-feature-x") so the run is easy to find later.
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

    /// <summary>
    /// When true, a Backup click writes ONE consolidated, re-runnable .sql
    /// file per endpoint (GO-separated batches) instead of the default
    /// folder-of-one-file-per-object layout. Handy when you want a single
    /// artifact to archive or hand off. Default false = the granular
    /// per-object layout the Scripts pane can re-execute selectively.
    /// </summary>
    [ObservableProperty] private bool _backupAsSingleScript;

    // Seeds StageAsDacpacBranch from settings once; prevents later refreshes
    // (e.g. after Settings "Save All") from clobbering the user's toggle.
    private bool _dacpacDefaultsApplied;

    public ObservableCollection<string>        Environments    { get; } = new();
    public ObservableCollection<string>        Databases       { get; } = new();
    public ObservableCollection<TargetPickVm>  Targets         { get; } = new();
    public ObservableCollection<BatchItem>     Items           { get; } = new();
    public ObservableCollection<BatchItem>     FilteredItems   { get; } = new();

    /// <summary>
    /// Checkable object-type facets for the Type column's funnel filter (same
    /// pattern as the Snapshots grid). Rebuilt as types resolve; every value
    /// defaults to ticked so the first view shows everything. A row whose type
    /// facet is unticked is hidden — and, like every other filter here, hidden
    /// rows are excluded from Execute.
    /// </summary>
    public ObservableCollection<DiffFilterValue> TypeFilterValues { get; } = new();

    /// <summary>
    /// Checkable sync-state facets for the state column's funnel filter — one per
    /// distinct <see cref="BatchSyncState"/> currently present (In sync ✓ / New ＋ /
    /// Out of sync ≠ / Partial ◐ / Not in source ⊘ / Not anywhere ✕). Each value's
    /// text carries its glyph, so the flyout doubles as the legend. Unticking a
    /// state hides those rows — and, like every filter here, hidden rows are
    /// excluded from Execute.
    /// </summary>
    public ObservableCollection<DiffFilterValue> StateFilterValues { get; } = new();

    /// <summary>Rows currently visible after the status / name / hide-in-sync filters.</summary>
    public int VisibleCount => FilteredItems.Count;

    /// <summary>Total rows loaded, before any filter.</summary>
    public int TotalObjectCount => Items.Count;

    /// <summary>
    /// Compact object-count summary for the items toolbar. Reads
    /// "N objects" when nothing is filtered out, and "N of M objects" when
    /// the status / name / hide-in-sync filters are hiding some rows — so
    /// the number always reflects what's actually on screen (and drops as
    /// soon as Hide in-sync trims the list).
    /// </summary>
    public string CountSummary => VisibleCount == TotalObjectCount
        ? $"{TotalObjectCount} object{(TotalObjectCount == 1 ? "" : "s")}"
        : $"{VisibleCount} of {TotalObjectCount} objects";

    /// <summary>Rows ticked across the whole list (including any currently hidden by a filter).</summary>
    public int SelectedCount => Items.Count(i => i.IsSelected);

    /// <summary>Ticked AND currently visible — the exact set Execute Selected will run.</summary>
    public int SelectedVisibleCount => FilteredItems.Count(i => i.IsSelected);

    /// <summary>Drives the selected-count chip's visibility (shown only when something is ticked).</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// Selected-count label for the toolbar. Reads "N selected" normally; when a
    /// filter is hiding some ticked rows it reads "M of N selected shown" so it's
    /// obvious the hidden ticks won't run — Execute Selected only touches the
    /// visible ticked rows.
    /// </summary>
    public string SelectionSummary
    {
        get
        {
            var total = SelectedCount;
            if (total == 0) return "";
            var shown = SelectedVisibleCount;
            return shown == total ? $"{total} selected" : $"{shown} of {total} selected shown";
        }
    }

    private void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(TotalObjectCount));
        OnPropertyChanged(nameof(CountSummary));
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedVisibleCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }

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

    /// <summary>Generation counter so an off-thread source-candidate rebuild that
    /// finishes late (superseded by a newer one) discards its stale result.</summary>
    private int _sourceCandGen;

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
        // Items list changed → previous IsInSync values are at best stale,
        // at worst wrong (new rows have no value yet). Cancel any
        // in-flight check pass and start a fresh one.
        QueueSyncCheckRefresh();
    }

    // ─── Background "already in sync?" pre-check ──────────────────────────
    //
    // For every item that has a source endpoint + at least one ticked target,
    // we fetch source.Hash and target.Hash off-thread and set IsInSync. Used
    // to render a small ✓ next to rows the user doesn't need to bother
    // executing — purely informational, never blocks the user or auto-skips
    // anything during Execute.
    //
    // Thread safety:
    //   • All BatchItem property writes go through Dispatcher.UIThread.Post
    //     so Avalonia bindings see the change on the UI thread.
    //   • _syncCheckGate caps concurrent catalog hits at 4 so a 500-row
    //     batch doesn't open 500 connections in parallel.
    //   • _syncCheckCts owns the cancellation token; Execute and any
    //     state change (source / targets / items) cancel the previous
    //     pass before starting a new one so two passes never race.

    private CancellationTokenSource? _syncCheckCts;
    private static readonly SemaphoreSlim _syncCheckGate = new(initialCount: 4, maxCount: 4);

    /// <summary>
    /// True while a background sync/type pass is running (including its debounce
    /// window). Drives a single, lightweight "Checking…" indicator so the blank
    /// badges during re-analysis read as "working", not "nothing". Ref-counted
    /// across overlapping/superseded passes so it flips on at the first and off
    /// only after the last — no per-row spinners, no continuous animation when idle.
    /// </summary>
    [ObservableProperty] private bool _isChecking;

    /// <summary>Count of in-flight check passes (0 ⇒ nothing running).</summary>
    private int _activeChecks;

    private async Task SetCheckingAsync(bool value)
        => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsChecking = value);

    /// <summary>
    /// Debounce target. Called from collection / property changes that
    /// alter the inputs of the pre-check; cancels any running pass and
    /// kicks a new one off on a background task. No-op when there are
    /// no items, no source, or no ticked targets.
    /// </summary>
    private void QueueSyncCheckRefresh()
    {
        _syncCheckCts?.Cancel();
        _syncCheckCts = new CancellationTokenSource();
        var ct = _syncCheckCts.Token;

        // Only show the "Checking…" indicator when a real query will happen
        // (there's a source + at least one row). Computed here on the UI thread.
        bool willQuery = Items.Count > 0
                         && !string.IsNullOrWhiteSpace(SourceEnv)
                         && !string.IsNullOrWhiteSpace(SourceDatabase);

        // Debounce: a single source/target/paste interaction fires several
        // property changes in a burst (dropdown cascades, target ticks, a
        // multi-line paste). Without a short settle delay each one would cancel
        // and restart a full catalog pass, hammering the DB and the dispatcher.
        // Waiting ~200ms coalesces the burst into one pass; the CTS cancels the
        // delay the moment the next change arrives.
        _ = Task.Run(async () =>
        {
            // Ref-count the indicator: on at the first in-flight pass, off after
            // the last, so overlapping/superseded passes don't flicker it.
            bool counted = false;
            if (willQuery)
            {
                counted = true;
                if (System.Threading.Interlocked.Increment(ref _activeChecks) == 1)
                    await SetCheckingAsync(true).ConfigureAwait(false);
            }
            try
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                await RunSyncChecksAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (counted && System.Threading.Interlocked.Decrement(ref _activeChecks) == 0)
                    await SetCheckingAsync(false).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>
    /// Public hook so callers (Execute click) can stop the background
    /// pre-check before issuing real syncs — the pre-check would just
    /// re-fetch the same catalog rows the sync is about to touch.
    /// </summary>
    public void CancelSyncCheck() => _syncCheckCts?.Cancel();

    /// <summary>
    /// Compare every item's source hash against each ticked target's
    /// hash; set IsInSync accordingly. Each row's check runs through a
    /// shared concurrency gate so big batches don't fan out into
    /// hundreds of simultaneous catalog reads. Cancellation is honored
    /// at every async point so stale passes drop their results cleanly.
    /// </summary>
    private async Task RunSyncChecksAsync(CancellationToken ct)
    {
        // Snapshot inputs on the UI thread before fanning out. Reading
        // ObservableCollections from worker threads isn't safe —
        // grabbing references + counts here keeps the worker
        // self-contained.
        BatchItem[] items;
        string? srcConn;
        List<string> targetConns;
        try
        {
            items = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Items.ToArray());
            srcConn = (SourceEnv is null || SourceDatabase is null)
                ? null
                : _svc.Connections.Get(SourceEnv, SourceDatabase);
            targetConns = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                Targets.Where(t => t.IsChecked)
                       .Select(t => _svc.Connections.Get(t.Environment, t.Database) ?? "")
                       .Where(c => !string.IsNullOrEmpty(c))
                       .ToList());
        }
        catch (OperationCanceledException) { return; }

        // ── Phase 1a: source catalog (types + membership) in ONE lock-free
        //    query, instead of a full-definition fetch per object. ──
        Dictionary<string, SqlObjectType>? srcTypes = null;
        if (!string.IsNullOrEmpty(srcConn))
        {
            try
            {
                var refs = await _svc.Scripter.ListAllAsync(srcConn!, ct).ConfigureAwait(false);
                srcTypes = new Dictionary<string, SqlObjectType>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in refs) srcTypes[MetaKey(r.Id)] = r.Type;
            }
            catch (OperationCanceledException) { return; }
            catch { srcTypes = null; }   // fall back to per-object fetch below
        }

        // Push types to the grid immediately — ONE UI hop for all rows — so the
        // Type column + facet fill in without waiting on the content phase.
        if (srcTypes is not null)
        {
            var typesLocal = srcTypes;
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var it in items)
                        it.ObjectType = TryMetaKey(it.Name, out var k) && typesLocal.TryGetValue(k, out var ty)
                            ? ty : (SqlObjectType?)null;
                    RebuildTypeFilterValues();
                });
            }
            catch (OperationCanceledException) { return; }
        }

        // No source or no targets → can't compute create / in-sync. Reset those
        // rows to Unknown (types stay when a source is present) and stop.
        if (string.IsNullOrEmpty(srcConn) || targetConns.Count == 0)
        {
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var it in items) { it.State = BatchSyncState.Unknown; it.SyncCheckHint = ""; }
                    if (string.IsNullOrEmpty(srcConn))
                    {
                        foreach (var it in items) it.ObjectType = null;
                        RebuildTypeFilterValues();
                    }
                    RebuildStateFilterValues();
                });
            }
            catch (OperationCanceledException) { }
            return;
        }

        // ── Phase 1b: target catalogs (membership) — one query each. ──
        List<HashSet<string>>? targetKeys = null;
        try
        {
            var sets = await Task.WhenAll(targetConns.Select(async tc =>
            {
                var refs = await _svc.Scripter.ListAllAsync(tc, ct).ConfigureAwait(false);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in refs) set.Add(MetaKey(r.Id));
                return set;
            })).ConfigureAwait(false);
            targetKeys = sets.ToList();
        }
        catch (OperationCanceledException) { return; }
        catch { targetKeys = null; }   // fall back to per-object fetch

        bool fastPath = srcTypes is not null && targetKeys is not null
                        && targetKeys.Count == targetConns.Count;

        int T = targetConns.Count;

        if (fastPath)
        {
            // Phase 1c: decide from membership alone everything that needs NO
            // content read — source-missing (⊘ / ✕) and will-create-on-all (＋) —
            // and apply it in ONE batched UI update. Rows present in the source AND
            // at least one target still need a content compare (only those can be
            // ✓ in sync, ≠ out of sync, or ◐ a match/differ mix), collected here
            // together with WHICH targets they're present in (absent ones need no
            // fetch — their count is already known).
            var needContent = new List<(BatchItem Item, ObjectIdentifier Id, List<string> Present, int Absent)>();
            var apply = new List<(BatchItem Item, BatchSyncState State, string Hint)>();

            foreach (var item in items)
            {
                if (!TryParseId(item.Name, out var id, out var key)) continue;

                var present = new List<string>();
                for (int i = 0; i < T; i++)
                    if (targetKeys![i].Contains(key)) present.Add(targetConns[i]);
                int absent = T - present.Count;

                if (!srcTypes!.ContainsKey(key))
                    apply.Add((item,
                        present.Count == 0 ? BatchSyncState.NotAnywhere : BatchSyncState.NotInSource,
                        present.Count == 0
                            ? "Not found in the source or any target"
                            : $"Missing from the source (exists in {present.Count} of {T} target{(T == 1 ? "" : "s")}) — nothing to push"));
                else if (absent == T)
                    apply.Add((item, BatchSyncState.WillCreate,
                        T == 1 ? "New — will be created on the target" : $"New — will be created on all {T} targets"));
                else
                    needContent.Add((item, id, present, absent));
            }

            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var (item, state, hint) in apply) { item.State = state; item.SyncCheckHint = hint; }
                });
            }
            catch (OperationCanceledException) { return; }

            // Phase 2: content compare — only the PRESENT targets of the rows that
            // need it. Concurrency-gated so a big batch doesn't fan out into
            // hundreds of simultaneous reads.
            var tasks = needContent.Select(p => Task.Run(async () =>
            {
                await _syncCheckGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var srcObj = await _svc.Scripter.GetObjectAsync(srcConn!, p.Id, ct).ConfigureAwait(false);
                    if (srcObj is null)
                    { await SetStateAsync(p.Item, BatchSyncState.NotInSource, "Source object not found").ConfigureAwait(false); return; }

                    int match = 0, differ = 0;
                    foreach (var tc in p.Present)
                    {
                        ct.ThrowIfCancellationRequested();
                        var tObj = await _svc.Scripter.GetObjectAsync(tc, p.Id, ct).ConfigureAwait(false);
                        if (tObj is null) differ++;   // vanished since the listing → treat as a diff
                        else if (string.Equals(tObj.Hash, srcObj.Hash, StringComparison.OrdinalIgnoreCase)) match++;
                        else differ++;
                    }
                    var (state, hint) = Classify(match, differ, p.Absent, T);
                    await SetStateAsync(p.Item, state, hint).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
                finally { _syncCheckGate.Release(); }
            }, ct)).ToArray();

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        else
        {
            // Fallback (catalog listing unavailable — older server / transient
            // error): the original per-object fetch, so correctness never
            // regresses. Concurrency-gated + cancellable.
            var tasks = items.Select(item => Task.Run(async () =>
            {
                await _syncCheckGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TryParseId(item.Name, out var id, out _)) return;

                    var srcObj = await _svc.Scripter.GetObjectAsync(srcConn!, id, ct).ConfigureAwait(false);
                    if (srcObj is null)
                    {
                        // Distinguish "not in source" from "not anywhere".
                        int presentCount = 0;
                        foreach (var tc in targetConns)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (await _svc.Scripter.GetObjectAsync(tc, id, ct).ConfigureAwait(false) is not null) presentCount++;
                        }
                        await SetStateAsync(item,
                            presentCount == 0 ? BatchSyncState.NotAnywhere : BatchSyncState.NotInSource,
                            presentCount == 0
                                ? "Not found in the source or any target"
                                : $"Missing from the source (exists in {presentCount} of {T} target{(T == 1 ? "" : "s")}) — nothing to push")
                            .ConfigureAwait(false);
                        return;
                    }
                    int match = 0, differ = 0, absent = 0;
                    foreach (var tc in targetConns)
                    {
                        ct.ThrowIfCancellationRequested();
                        var tObj = await _svc.Scripter.GetObjectAsync(tc, id, ct).ConfigureAwait(false);
                        if (tObj is null) absent++;
                        else if (string.Equals(tObj.Hash, srcObj.Hash, StringComparison.OrdinalIgnoreCase)) match++;
                        else differ++;
                    }
                    var (state, hint) = Classify(match, differ, absent, T);
                    await SetStateAsync(item, state, hint).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
                finally { _syncCheckGate.Release(); }
            }, ct)).ToArray();

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Rebuild the Sync-state facet list to whatever states resolved, and — if
        // Hide-in-sync is on, or the grid is sorted/filtered by Sync state — the
        // filtered/sorted view too, so it reflects the new states.
        if (!ct.IsCancellationRequested)
        {
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RebuildStateFilterValues();
                    if (HideInSync || Sorter.ActiveKey == "Sync" || StateFilterValues.Any(v => !v.IsIncluded))
                        RebuildFilteredItems();
                });
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    /// <summary>Collapse per-target tallies into a single <see cref="BatchSyncState"/>
    /// plus a human breakdown for the tooltip. <paramref name="match"/> +
    /// <paramref name="differ"/> + <paramref name="absent"/> == the target count.</summary>
    private static (BatchSyncState State, string Hint) Classify(int match, int differ, int absent, int total)
    {
        if (absent == total)
            return (BatchSyncState.WillCreate,
                total == 1 ? "New — will be created on the target" : $"New — will be created on all {total} targets");
        if (differ == 0 && absent == 0)
            return (BatchSyncState.InSync,
                total == 1 ? "In sync with the target" : $"In sync with all {total} targets");
        if (match == 0 && absent == 0)
            return (BatchSyncState.OutOfSync,
                total == 1 ? "Differs from the target — will be altered" : $"Differs from all {total} targets — will be altered");

        // Mixed across targets — spell out the split.
        var parts = new List<string>(3);
        if (match  > 0) parts.Add($"{match} in sync");
        if (differ > 0) parts.Add($"{differ} differ");
        if (absent > 0) parts.Add($"{absent} will create");
        return (BatchSyncState.Partial, string.Join(" · ", parts));
    }

    /// <summary>
    /// Marshals a State + hint update onto the UI thread so Avalonia bindings see
    /// the change from the dispatcher. Fire-and-forget — the caller doesn't depend
    /// on ordering, only on each update eventually landing.
    /// </summary>
    private static Task SetStateAsync(BatchItem item, BatchSyncState state, string hint)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            item.State         = state;
            item.SyncCheckHint = hint;
        });
        return Task.CompletedTask;
    }

    // Catalog key helpers — normalise (schema, name) so the source list, each
    // target list, and every row's typed name all compare on one
    // casing-insensitive key.
    private static string MetaKey(ObjectIdentifier id) =>
        $"{id.Schema.ToUpperInvariant()}|{id.Name.ToUpperInvariant()}";

    private static bool TryParseId(string name, out ObjectIdentifier id, out string key)
    {
        id = default; key = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        try { id = ObjectIdentifier.Parse(name.Trim()); } catch { return false; }
        key = MetaKey(id);
        return true;
    }

    private static bool TryMetaKey(string name, out string key)
        => TryParseId(name, out _, out key);

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
        // Selection or visible-set changed → refresh the "N selected" toolbar chip.
        NotifySelectionChanged();
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
    partial void OnHideInSyncChanged(bool value)     => RebuildFilteredItems();

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
        // Object-type facet: the set of ticked type labels. A row is hidden only
        // when its type is KNOWN and its facet is unticked — rows whose type
        // hasn't resolved yet (blank label) stay visible, so nothing is silently
        // dropped while the fast metadata pass is still running.
        var allowedTypes = TypeFilterValues.Where(v => v.IsIncluded)
                                           .Select(v => v.Value)
                                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedStates = StateFilterValues.Where(v => v.IsIncluded)
                                             .Select(v => v.Value)
                                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = new List<BatchItem>();
        foreach (var it in Items)
        {
            if (want is not null && it.Status != want) continue;
            if (nameNeedle.Length > 0 &&
                !it.Name.Contains(nameNeedle, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TypeFilterValues.Count > 0 && !string.IsNullOrEmpty(it.TypeLabel) &&
                !allowedTypes.Contains(it.TypeLabel))
                continue;
            // Sync-state facet — same rule: hide only when the state is KNOWN
            // (not Unknown) and its facet is unticked; unresolved rows stay.
            if (StateFilterValues.Count > 0 && it.State != BatchSyncState.Unknown &&
                !allowedStates.Contains(it.StateLabel))
                continue;
            // Hide already-in-sync rows when the toggle is on. Only rows
            // KNOWN to be in sync (IsInSync == true) are hidden — rows that
            // are unknown (null, e.g. sync-check hasn't run or source not
            // found) stay visible so nothing is silently dropped.
            if (HideInSync && it.IsInSync == true) continue;
            matched.Add(it);
        }
        foreach (var it in Sorter.Apply(matched, SortSelectors))
            FilteredItems.Add(it);
        // Filter changed → visible set changed → header glyph may need to flip.
        RefreshSelectAllState();
        // …and the visible/total object count shown in the toolbar.
        NotifyCountsChanged();
    }

    /// <summary>
    /// Rebuild the distinct object-type facets shown in the Type column's funnel
    /// filter, from whatever types have resolved so far. Preserves the user's
    /// existing tick state across rebuilds (so resolving more types doesn't
    /// silently re-include a type they'd unticked), and defaults newly-seen
    /// types to ticked. Call on the UI thread.
    /// </summary>
    private void RebuildTypeFilterValues()
    {
        var present = Items.Select(i => i.TypeLabel)
                           .Where(l => !string.IsNullOrEmpty(l))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                           .ToList();

        // Remember prior tick state so a rebuild doesn't reset the user's choices.
        var prior = TypeFilterValues.ToDictionary(v => v.Value, v => v.IsIncluded, StringComparer.OrdinalIgnoreCase);

        foreach (var v in TypeFilterValues) v.PropertyChanged -= OnTypeFilterValueChanged;
        TypeFilterValues.Clear();
        foreach (var label in present)
        {
            var v = new DiffFilterValue(label) { IsIncluded = !prior.TryGetValue(label, out var was) || was };
            v.PropertyChanged += OnTypeFilterValueChanged;
            TypeFilterValues.Add(v);
        }
    }

    private void OnTypeFilterValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffFilterValue.IsIncluded)) RebuildFilteredItems();
    }

    /// <summary>
    /// Rebuild the sync-state facets from whatever states have resolved, in a
    /// fixed, meaningful order (most-actionable first). Preserves the user's
    /// tick state across rebuilds; defaults newly-seen states to ticked. The
    /// value text carries the glyph so the flyout is also the legend. Call on
    /// the UI thread.
    /// </summary>
    private void RebuildStateFilterValues()
    {
        // Fixed display order, filtered to the states actually present.
        var order = new[]
        {
            BatchSyncState.OutOfSync, BatchSyncState.WillCreate, BatchSyncState.Partial,
            BatchSyncState.InSync, BatchSyncState.NotInSource, BatchSyncState.NotAnywhere,
        };
        var present = new HashSet<BatchSyncState>(Items.Select(i => i.State).Where(s => s != BatchSyncState.Unknown));

        var prior = StateFilterValues.ToDictionary(v => v.Value, v => v.IsIncluded, StringComparer.OrdinalIgnoreCase);

        foreach (var v in StateFilterValues) v.PropertyChanged -= OnStateFilterValueChanged;
        StateFilterValues.Clear();
        foreach (var s in order)
        {
            if (!present.Contains(s)) continue;
            var label = BatchSyncStateDisplay.Label(s);
            var v = new DiffFilterValue(label) { IsIncluded = !prior.TryGetValue(label, out var was) || was };
            v.PropertyChanged += OnStateFilterValueChanged;
            StateFilterValues.Add(v);
        }
    }

    private void OnStateFilterValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffFilterValue.IsIncluded)) RebuildFilteredItems();
    }

    // ─────────────────────────── Sorting ───────────────────────────

    private static readonly IReadOnlyDictionary<string, Func<BatchItem, object?>> SortSelectors =
        new Dictionary<string, Func<BatchItem, object?>>
        {
            ["Name"]   = i => i.Name,
            ["Status"] = i => i.Status.ToString(),
            // Sync state — clusters similar rows together so a whole group can be
            // acted on at once. Rank puts the most-actionable first and the
            // settled / absent rows last: out-of-sync → new → partial → in sync →
            // not-in-source → not-anywhere → unknown. Ascending = that order; Desc flips it.
            ["Sync"]   = i => i.State switch
            {
                BatchSyncState.OutOfSync   => 0,
                BatchSyncState.WillCreate  => 1,
                BatchSyncState.Partial     => 2,
                BatchSyncState.InSync      => 3,
                BatchSyncState.NotInSource => 4,
                BatchSyncState.NotAnywhere => 5,
                _                          => 6,
            },
        };

    public string NameSortIndicator   => Sorter.Indicator("Name");
    public string StatusSortIndicator => Sorter.Indicator("Status");
    public string SyncSortIndicator   => Sorter.Indicator("Sync");

    /// <summary>Header glyph for the sync (✓/＋) column: the active ▲/▼ arrow when
    /// it's the sort key, else a faint up-down hint (⇅) so it reads as sortable.</summary>
    public string SyncSortGlyph =>
        Sorter.Indicator("Sync") is { Length: > 0 } ind ? ind.Trim() : "⇅";

    /// <summary>Label for the "Sort by state" entry inside the state-column flyout,
    /// carrying the active ▲/▼ so the current direction is visible there.</summary>
    public string SyncSortMenuLabel => $"Sort by state{Sorter.Indicator("Sync")}";

    /// <summary>Header click → cycle the column's sort and rebuild the visible (sorted) rows.</summary>
    [RelayCommand]
    private void ToggleSort(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        Sorter.Toggle(key);
        RebuildFilteredItems();
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(StatusSortIndicator));
        OnPropertyChanged(nameof(SyncSortIndicator));
        OnPropertyChanged(nameof(SyncSortGlyph));
        OnPropertyChanged(nameof(SyncSortMenuLabel));
    }

    // ───────────────────────── CSV export ──────────────────────────

    public string CsvSuggestedFileName => "batch.csv";

    public IReadOnlyList<string> CsvHeaders { get; } =
        new[] { "#", "Object name", "Status", "Message" };

    public bool HasExportableRows => FilteredItems.Count > 0;

    /// <summary>Exports the currently-visible rows (after filter + sort), in display order.</summary>
    public IEnumerable<IReadOnlyList<string?>> CsvRows() =>
        FilteredItems.Select(i => (IReadOnlyList<string?>)new[]
        {
            i.Index.ToString(),
            i.Name,
            i.Status.ToString(),
            i.Message,
        });

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

        // Snapshot the inputs on the UI thread (Endpoints + ticked membership),
        // then do the DISK work (open each endpoint's schema store + enumerate
        // its snapshots — which touches the file system, 3× Directory.Create per
        // store) off the UI thread. Doing it inline was a real "stick" on every
        // source/target change, especially when %AppData% is on a synced/network
        // drive. A generation counter discards a rebuild that a newer one
        // superseded, so the picker never flickers to a stale list.
        var eps = Endpoints.ToArray();
        var tickedLive = new HashSet<string>(
            eps.Where(matchesTicked).Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        int gen = ++_sourceCandGen;

        _ = Task.Run(() =>
        {
            var built = new List<BatchSourceItem>(eps.Length);
            foreach (var ep in eps)
            {
                if (!tickedLive.Contains(ep.Key))
                    built.Add(new BatchSourceItem(ep, Snapshot: null));
                try
                {
                    var store = _svc.OpenSchemaStore(ep.Environment, ep.Database);
                    foreach (var snap in store.ListSnapshots())
                        built.Add(new BatchSourceItem(ep, snap));
                }
                catch { /* store unreadable — skip, source picker should never blow up */ }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (gen != _sourceCandGen) return;   // a newer rebuild superseded this one
                SourceCandidates.Clear();
                foreach (var it in built) SourceCandidates.Add(it);

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
            });
        });
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
        // The target set (or the source, which rebuilds targets) just changed, so
        // every row's sync state was measured against a DIFFERENT set and is now
        // stale. Clear the badges immediately — a wrong ✓/≠/◐ lingering during the
        // re-check window is worse than a blank — then re-run the check.
        InvalidateSyncStates();
        QueueSyncCheckRefresh();
    }

    /// <summary>
    /// Wipe every row's sync state back to Unknown (clears the badge + tooltip)
    /// so no stale symbol shows while a fresh check runs. Types are left as-is
    /// (the fast pass refreshes them, and clearing would just flicker the column).
    /// Re-shows anything the state filter / Hide-in-sync had hidden, since it's
    /// all "unknown" again until the pass resolves it. UI thread.
    /// </summary>
    private void InvalidateSyncStates()
    {
        bool any = false;
        foreach (var it in Items)
        {
            if (it.State != BatchSyncState.Unknown || it.SyncCheckHint.Length > 0)
            {
                it.State = BatchSyncState.Unknown;
                it.SyncCheckHint = "";
                any = true;
            }
        }
        if (any) RebuildFilteredItems();
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
                        var snapVm = BatchPreviewViewModel.ForFileAndTargets(
                            svc:         _svc,
                            sourceLabel: $"Source · {SourceEnv} / {SourceDatabase} @ {snapLabel}",
                            fileContent: sourceSql!,
                            objectName:  item.Name.Trim(),
                            targets:     targetEndpoints);
                        snapVm.AutoIgnoreWhitespaceForInSync = true;
                        return snapVm;
                    }
                }
            }
            // Snapshot mode but the object isn't in this snapshot — fall
            // through so the user at least sees the targets, with a
            // "not in snapshot" placeholder on the source side.
            var missingVm = BatchPreviewViewModel.ForFileAndTargets(
                svc:         _svc,
                sourceLabel: $"Source · {SourceEnv} / {SourceDatabase} (snapshot)",
                fileContent: $"-- '{item.Name}' is not present in the selected snapshot.",
                objectName:  item.Name.Trim(),
                targets:     targetEndpoints);
            missingVm.AutoIgnoreWhitespaceForInSync = true;
            return missingVm;
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
        var liveVm = new BatchPreviewViewModel(_svc, item.Name.Trim(), endpoints);
        liveVm.AutoIgnoreWhitespaceForInSync = true;
        return liveVm;
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
    private Task ExecuteSelectedAsync()
    {
        // Selected AND visible: a row ticked but then hidden by a filter
        // (Status / name / Hide in-sync) is excluded — hidden rows are never
        // executed, the same "run only what you can see" rule Execute follows.
        // This is why selection and filter must intersect: the checkbox picks
        // rows, the filter scopes them, and Execute Selected runs the overlap.
        var visibleSelected = FilteredItems.Where(i => i.IsSelected).ToList();
        if (visibleSelected.Count == 0 && Items.Any(i => i.IsSelected))
        {
            // Ticks exist but the filter hides every one — say so precisely
            // instead of the generic "tick rows first".
            Status = "All ticked rows are hidden by the current filter — nothing to run.";
            _svc.Toasts.Warning("Selection hidden",
                "Every ticked row is filtered out. Clear the filter or tick a visible row.");
            return Task.CompletedTask;
        }
        return ExecuteCoreAsync(visibleSelected,
            emptyMsg: "Tick rows first",
            scopeLabel: "selected rows");
    }

    private async Task ExecuteCoreAsync(List<BatchItem> work, string emptyMsg, string scopeLabel)
    {
        if (work.Count == 0)
        {
            Status = $"No {scopeLabel} to execute.";
            _svc.Toasts.Warning(emptyMsg, $"Nothing in {scopeLabel} to run.");
            return;
        }

        // Free the background pre-check pool before we issue real syncs —
        // the check would just re-fetch what the sync is about to mutate,
        // so cancelling here keeps the worker thread + concurrency gate
        // available for the actual work.
        CancelSyncCheck();

        // Major-action gate: Execute mutates every ticked target. Confirm
        // before running so a stray click doesn't push 30 procs to PROD.
        // Spelled out in the message: rows × targets so the user sees the
        // real blast radius before saying yes.
        var targetCount = Targets.Count(t => t.IsChecked);
        var rowsLabel    = work.Count   == 1 ? "object"  : "objects";
        var targetsLabel = targetCount  == 1 ? "target"  : "targets";
        var scopeWord    = scopeLabel.StartsWith("filtered") ? "visible" : "selected";
        var scopeLine    = $"Execute {work.Count} {scopeWord} {rowsLabel} on {targetCount} {targetsLabel}?";
        var ok = await ConfirmDialog.AskAsync(
            title:       "Execute batch?",
            message:     $"{scopeLine}\n\nExisting objects will be altered and missing objects created on each target. "
                       + "Each target is backed up first. This can't be undone automatically.",
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
                            // approvedDestructiveAlters: null — Batch runs
                            // unattended. SyncService will apply the safe
                            // ALTER subset and skip every destructive step;
                            // we surface the skipped count below so the
                            // user knows which rows still need a single-
                            // execute pass on the Sync screen.
                            var r = await _svc.Sync.SyncAsync(
                                srcConn!, tgtConn!, id, SourceEnv!, t.Environment,
                                ct: default, zipPair: false,
                                captureSourceBackup: false,
                                runStamp: batchRunStamp,
                                approvedDestructiveAlters: null);
                            if (r.TargetBackupPath is not null) batchBackupPaths.Add(r.TargetBackupPath);
                            switch (r.Status)
                            {
                                case SyncStatus.Success:
                                    // Surface ALTER-skipped-destructive counts in
                                    // the row's message so the user can spot
                                    // tables that need attention without
                                    // opening logs. Plain "ok" otherwise.
                                    var okMsg = r.SkippedDestructiveCount > 0
                                        ? $"[{t.Environment}·{t.Database}] ok ({r.SkippedDestructiveCount} destructive change(s) skipped — review on Sync screen)"
                                        : $"[{t.Environment}·{t.Database}] ok";
                                    perTargetMsgs.Add(okMsg);
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
            // The run just changed the targets, so the in-sync (✓) answers
            // from before Execute are now stale — rows we just pushed should
            // flip to in-sync. Kick a fresh check so the ticks (and the
            // Hide in-sync filter) reflect reality without the user having to
            // touch the source/target pickers to force a refresh.
            QueueSyncCheckRefresh();
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
            if (BackupAsSingleScript)
                await BackupAsSingleScriptCoreAsync(srcConn, checkedTargets, backupRunStamp);
            else
                await BackupAsFilesCoreAsync(srcConn, checkedTargets, backupRunStamp);
        }
        finally
        {
            IsBusy = false;
            _suppressFilterRebuild = false;
            RebuildFilteredItems();
        }
    }

    /// <summary>Default backup: one file per object under a run folder.</summary>
    private async Task BackupAsFilesCoreAsync(
        string? srcConn, List<TargetPickVm> checkedTargets, string backupRunStamp)
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

    /// <summary>
    /// Single-script backup: fetch every object from every endpoint, then
    /// write ONE consolidated .sql per endpoint. Per-row status still
    /// reflects whether the object was found (Success) or missing
    /// everywhere (Skipped), so the grid stays informative.
    /// </summary>
    private async Task BackupAsSingleScriptCoreAsync(
        string? srcConn, List<TargetPickVm> checkedTargets, string backupRunStamp)
    {
        // Endpoints in a stable order: source first, then each ticked target.
        var endpoints = new List<(string Conn, string Env, Base.It.Core.Backup.BackupRole Role, string Label)>();
        if (!string.IsNullOrWhiteSpace(srcConn))
            endpoints.Add((srcConn!, SourceEnv!, Base.It.Core.Backup.BackupRole.Source, SourceEnv!));
        foreach (var t in checkedTargets)
        {
            var conn = _svc.Connections.Get(t.Environment, t.Database);
            if (!string.IsNullOrWhiteSpace(conn))
                endpoints.Add((conn!, t.Environment, Base.It.Core.Backup.BackupRole.Target, $"{t.Environment}·{t.Database}"));
        }
        if (endpoints.Count == 0) { Status = "No reachable endpoints to back up."; return; }

        // One accumulation bucket per endpoint, index-aligned with `endpoints`.
        var bundles = endpoints
            .Select(_ => new List<(SqlObjectType Type, ObjectIdentifier Id, string Definition)>())
            .ToList();

        foreach (var item in Items.ToList())
        {
            item.Status = BatchStatus.Running; item.Message = "";
            try
            {
                var id = ObjectIdentifier.Parse(item.Name.Trim());
                var msgs = new List<string>();
                int hits = 0, misses = 0;

                for (int i = 0; i < endpoints.Count; i++)
                {
                    var ep = endpoints[i];
                    var obj = await _svc.Scripter.GetObjectAsync(ep.Conn, id);
                    if (obj is not null)
                    {
                        bundles[i].Add((obj.Type, id, obj.Definition));
                        msgs.Add($"[{ep.Label}] captured");
                        hits++;
                    }
                    else
                    {
                        msgs.Add($"[{ep.Label}] not found");
                        misses++;
                    }
                }

                item.Message = string.Join(" | ", msgs);
                if (hits > 0)        { item.Status = BatchStatus.Success; SuccessCount++; }
                else if (misses > 0) { item.Status = BatchStatus.Skipped; }
                else                 { item.Status = BatchStatus.Failed; FailCount++; }
            }
            catch (Exception ex)
            {
                item.Status = BatchStatus.Failed; item.Message = ex.Message; FailCount++;
            }
        }

        // Flush one consolidated script per endpoint.
        var writtenFiles = new List<string>();
        for (int i = 0; i < endpoints.Count; i++)
        {
            var path = _svc.Backups.WriteScript(
                backupRunStamp, endpoints[i].Role, endpoints[i].Env, bundles[i]);
            if (path is not null) writtenFiles.Add(path);
        }

        Status = writtenFiles.Count > 0
            ? $"Backup complete — {writtenFiles.Count} single-script file(s). Objects saved: {SuccessCount}, failed: {FailCount}."
            : $"Backup produced no files (nothing found). Failed: {FailCount}.";
        if (writtenFiles.Count > 0 && FailCount == 0)
            _svc.Toasts.Success("Backup complete", $"{writtenFiles.Count} script file(s) · {SuccessCount} object(s).");
        else if (FailCount > 0)
            _svc.Toasts.Warning("Backup finished with errors", $"{writtenFiles.Count} file(s) · {FailCount} failed.");
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
