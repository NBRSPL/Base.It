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

public sealed partial class BatchItem : ObservableObject
{
    [ObservableProperty] private bool        _isSelected;
    [ObservableProperty] private int         _index;
    [ObservableProperty] private string      _name    = "";
    [ObservableProperty] private BatchStatus _status  = BatchStatus.Pending;
    [ObservableProperty] private string      _message = "";
    public BatchItem(string name) { _name = name; }
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

    public bool CanSwap =>
        SelectedSourceEndpoint is not null && Targets.Count(t => t.IsChecked) == 1;

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

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchItem.Status))
            RebuildFilteredItems();
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

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            if (string.Equals(cfg.Environment, SourceEnv, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cfg.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
                continue;

            var keyCheck = $"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}";
            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains(keyCheck));
            pick.PropertyChanged += OnTargetPropertyChanged;
            Targets.Add(pick);
        }
        RebuildFilteredTargets();
        NotifyTargetCounts();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetPickVm.IsChecked))
            NotifyTargetCounts();
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

    [RelayCommand]
    private void LoadFromFile()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            Status = "Pick a CSV or XLSX with an 'Object name' column.";
            _svc.Toasts.Warning("Pick a file", "Provide a .csv or .xlsx with an 'Object name' column.");
            return;
        }
        try
        {
            var names = ObjectListLoader.FromFile(FilePath);
            Items.Clear();
            foreach (var n in names) Items.Add(new BatchItem(n));
            Status = $"Loaded {Items.Count} objects from {Path.GetFileName(FilePath)}.";
            _svc.Toasts.Success("List loaded", $"{Items.Count} object(s) from {Path.GetFileName(FilePath)}.");
        }
        catch (Exception ex)
        {
            Status = $"Load failed: {ex.Message}";
            _svc.Toasts.Error("Load failed", ex.Message);
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
    public BatchPreviewViewModel? BuildPreview(BatchItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Name)) return null;
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase))
            return null;

        var endpoints = new List<PreviewEndpoint>();

        var srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!) ?? "";
        endpoints.Add(new PreviewEndpoint(
            Label:            $"Source · {SourceEnv} / {SourceDatabase}",
            Color:            SourceProfile?.Color,
            ConnectionString: srcConn));

        foreach (var t in Targets.Where(t => t.IsChecked))
        {
            var tgtConn  = _svc.Connections.Get(t.Environment, t.Database) ?? "";
            var profile  = _svc.Connections.GetProfile(t.Environment, t.Database);
            endpoints.Add(new PreviewEndpoint(
                Label:            $"Target · {t.Environment} / {t.Database}",
                Color:            profile?.Color,
                ConnectionString: tgtConn));
        }

        return new BatchPreviewViewModel(_svc, item.Name.Trim(), endpoints);
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

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (Items.Count == 0)
        {
            Status = "No objects to execute.";
            _svc.Toasts.Warning("Nothing to run", "Add rows to the list or load a CSV / XLSX first.");
            return;
        }
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

        var srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!);
        if (string.IsNullOrWhiteSpace(srcConn))
        {
            Status = "Missing source connection string.";
            _svc.Toasts.Error("No source connection", $"{SourceEnv}·{SourceDatabase} isn't configured.");
            return;
        }

        var exporter      = await _svc.TryBuildDacpacExporterAsync();
        var exportedPaths = new List<string>();

        // Collect every backup file written during the batch so we can
        // produce a single consolidated zip at the end instead of one
        // tiny zip per object × target.
        var batchBackupPaths = new List<string>();

        IsBusy = true; SuccessCount = FailCount = 0;
        // ONE stamp for the whole batch click — every source + target
        // backup file lands under the same {date}\{stamp}_*\... tree so
        // a Scripts-pane revert can target one folder and run cleanly.
        var batchRunStamp = Base.It.Core.Backup.FileBackupStore.NewRunStamp();
        try
        {
            foreach (var item in Items.ToList())
            {
                item.Status  = BatchStatus.Running;
                item.Message = "";
                var perTargetMsgs = new List<string>();
                int itemOk = 0, itemFail = 0, itemNotFound = 0;

                try
                {
                    var id = ObjectIdentifier.Parse(item.Name.Trim());

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
        finally { IsBusy = false; }
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

        IsBusy = true; SuccessCount = FailCount = 0;
        // One stamp for the whole Backup click — same grouping rule as
        // Execute, just without ALTER on targets.
        var backupRunStamp = Base.It.Core.Backup.FileBackupStore.NewRunStamp();
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
        finally { IsBusy = false; }
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
