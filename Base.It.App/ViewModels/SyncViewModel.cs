using System.Collections.ObjectModel;
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

    /// <summary>
    /// Rebuild the target list from the active connection group (or every
    /// connection when no group is active), minus the source endpoint.
    /// Preserves existing IsChecked state across rebuilds.
    /// </summary>
    private void RebuildTargets()
    {
        var previouslyChecked = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();
        Targets.Clear();

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            if (string.Equals(cfg.Environment, SourceEnv, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cfg.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
                continue;

            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains($"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}"));
            Targets.Add(pick);
        }
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

        var srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!);
        if (string.IsNullOrWhiteSpace(srcConn))
        {
            Status = "No connection string for source.";
            _svc.Toasts.Error("No source connection", $"{SourceEnv}·{SourceDatabase} isn't configured.");
            return;
        }

        // Build the DACPAC exporter once per run so we don't re-read the
        // config file for each target.
        var exporter      = await _svc.TryBuildDacpacExporterAsync();
        var exportedPaths = new List<string>();
        bool anyTargetSucceeded = false;

        IsBusy = true; LastZipPath = "";
        try
        {
            var id = ObjectIdentifier.Parse(ObjectName.Trim());
            var parts = new List<string>();
            int ok = 0, fail = 0, notFound = 0;

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
                    var r = await _svc.Sync.SyncAsync(srcConn!, tgtConn!, id, SourceEnv!, t.Environment);
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
                    // DACPAC path: rich table script for SSDT output.
                    var src = await _svc.Scripter.GetObjectForDacpacAsync(srcConn!, id);
                    if (src is not null)
                    {
                        var path = exporter.Export(src.Id, src.Type, src.Definition);
                        if (path is not null)
                        {
                            exportedPaths.Add(path);
                            parts.Add($"[DACPAC] {System.IO.Path.GetRelativePath(exporter.Options.RootFolder, path)}");
                        }
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

            if (!string.IsNullOrWhiteSpace(SourceEnv))
            {
                var conn = _svc.Connections.Get(SourceEnv!, SourceDatabase!);
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    var r = await _svc.Backup.BackupAsync(conn!, SourceEnv!, id);
                    parts.Add(FormatPart(SourceEnv!, r));
                }
            }

            foreach (var t in Targets.Where(t => t.IsChecked))
            {
                var conn = _svc.Connections.Get(t.Environment, t.Database);
                if (string.IsNullOrWhiteSpace(conn)) continue;
                var r = await _svc.Backup.BackupAsync(conn!, t.Environment, id);
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
}
