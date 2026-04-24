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
    [ObservableProperty] private string _manualObject = "";
    [ObservableProperty] private string _statusFilter = "All";
    [ObservableProperty] private int _successCount;
    [ObservableProperty] private int _failCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "No objects loaded.";
    [ObservableProperty] private EnvironmentConfig? _sourceProfile;

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
        foreach (var it in Items)
            if (want is null || it.Status == want) FilteredItems.Add(it);
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
        SourceEnv      ??= Environments.FirstOrDefault();
        SourceDatabase ??= Databases.FirstOrDefault();
        RefreshProfiles();
        RebuildTargets();
    }

    partial void OnSourceEnvChanged(string? value)      { RefreshProfiles(); RebuildTargets(); }
    partial void OnSourceDatabaseChanged(string? value) { RefreshProfiles(); RebuildTargets(); }

    private void RefreshProfiles()
    {
        SourceProfile = (SourceEnv is null || SourceDatabase is null)
            ? null : _svc.Connections.GetProfile(SourceEnv, SourceDatabase);
    }

    private void RebuildTargets()
    {
        var previouslyChecked = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();
        Targets.Clear();

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            if (string.Equals(cfg.Environment, SourceEnv, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cfg.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
                continue;

            var keyCheck = $"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}";
            Targets.Add(TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains(keyCheck)));
        }
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
    private void AddManual()
    {
        var n = ManualObject.Trim();
        if (string.IsNullOrWhiteSpace(n))
        {
            _svc.Toasts.Warning("Nothing to add", "Type an object name into the 'Manual object' field first.");
            return;
        }
        if (Items.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            _svc.Toasts.Warning("Already in the list", $"'{n}' is already in the batch.");
            return;
        }
        Items.Add(new BatchItem(n));
        ManualObject = "";
        Status = $"Added {n}. Total: {Items.Count}.";
        _svc.Toasts.Success("Added", $"{n} · {Items.Count} total.");
    }

    [RelayCommand]
    private void RemoveSelected(BatchItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
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
                            var r = await _svc.Sync.SyncAsync(
                                srcConn!, tgtConn!, id, SourceEnv!, t.Environment,
                                ct: default, zipPair: false);
                            if (r.SourceBackupPath is not null) batchBackupPaths.Add(r.SourceBackupPath);
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
                            // DACPAC path: rich table script for SSDT output.
                            var src = await _svc.Scripter.GetObjectForDacpacAsync(srcConn!, id);
                            if (src is not null)
                            {
                                var preRel = exporter.RelativePathFor(id, src.Type);
                                var existedBefore = File.Exists(
                                    Path.Combine(exporter.Options.RootFolder, preRel));
                                var path = exporter.Export(id, src.Type, src.Definition);
                                if (path is not null)
                                {
                                    exportedPaths.Add(path);
                                    var rel = Path.GetRelativePath(exporter.Options.RootFolder, path);
                                    perTargetMsgs.Add(existedBefore
                                        ? $"[DACPAC] updated {rel}"
                                        : $"[DACPAC] created (new) {rel}");
                                }
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
                        var r = await _svc.Backup.BackupAsync(srcConn!, SourceEnv!, id);
                        Tally(r, msgs, ref hits, ref misses, SourceEnv!);
                    }

                    foreach (var t in checkedTargets)
                    {
                        var conn = _svc.Connections.Get(t.Environment, t.Database);
                        if (string.IsNullOrWhiteSpace(conn)) continue;
                        var r = await _svc.Backup.BackupAsync(conn!, t.Environment, id);
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
