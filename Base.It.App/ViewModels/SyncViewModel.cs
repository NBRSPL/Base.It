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

    /// <summary>Swap is meaningful only when there's exactly one ticked target to swap with.</summary>
    public bool CanSwap =>
        SelectedSourceEndpoint is not null && Targets.Count(t => t.IsChecked) == 1;

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

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            if (string.Equals(cfg.Environment, SourceEnv, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cfg.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
                continue;

            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains($"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}"));
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

            // One run-stamp groups every backup file (source + each
            // target) under the same dated folder. SyncAsync writes a
            // source-side backup on every call by default; we capture
            // it exactly once here and pass captureSourceBackup=false
            // through the loop so the source folder doesn't accumulate
            // N identical copies.
            var runStamp = Base.It.Core.Backup.FileBackupStore.NewRunStamp();
            string? sourceBackupPath = null;
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
                    var r = await _svc.Sync.SyncAsync(
                        srcConn!, tgtConn!, id, SourceEnv!, t.Environment,
                        ct: default, zipPair: true,
                        captureSourceBackup: false,
                        runStamp: runStamp);
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
    public BatchPreviewViewModel? BuildPreview()
    {
        if (string.IsNullOrWhiteSpace(SourceEnv) || string.IsNullOrWhiteSpace(SourceDatabase)) return null;
        if (string.IsNullOrWhiteSpace(ObjectName)) return null;

        var endpoints = new List<PreviewEndpoint>();

        var srcConn = _svc.Connections.Get(SourceEnv!, SourceDatabase!) ?? "";
        endpoints.Add(new PreviewEndpoint(
            Label:            $"Source · {SourceEnv} / {SourceDatabase}",
            Color:            SourceProfile?.Color,
            ConnectionString: srcConn));

        foreach (var t in Targets.Where(t => t.IsChecked))
        {
            var tgtConn = _svc.Connections.Get(t.Environment, t.Database) ?? "";
            var profile = _svc.Connections.GetProfile(t.Environment, t.Database);
            endpoints.Add(new PreviewEndpoint(
                Label:            $"Target · {t.Environment} / {t.Database}",
                Color:            profile?.Color,
                ConnectionString: tgtConn));
        }

        if (endpoints.Count < 2) return null; // source-only preview is pointless

        return new BatchPreviewViewModel(_svc, ObjectName.Trim(), endpoints);
    }

    /// <summary>
    /// Preview command — opens the same diff window Batch uses with the
    /// current source + ticked targets for <see cref="ObjectName"/>. Pure
    /// read; no execution. Wired from the view's code-behind, which owns
    /// the Window instance.
    /// </summary>
    [RelayCommand]
    private void Preview()
    {
        var preview = BuildPreview();
        if (preview is null)
        {
            _svc.Toasts.Warning("Nothing to preview", "Pick source, type an object name, and tick at least one target.");
            return;
        }
        PreviewRequested?.Invoke(preview);
    }

    /// <summary>Raised when <see cref="PreviewCommand"/> wants the host view to open a preview window.</summary>
    public event Action<BatchPreviewViewModel>? PreviewRequested;
}
