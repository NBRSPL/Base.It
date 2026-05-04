using System.Collections.ObjectModel;
using System.ComponentModel;
using Base.It.App.Services;
using Base.It.Core.Sql;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// One row in the Scripts pane: a .sql file picked from disk, executed
/// against every ticked target on Execute. Status mirrors Batch's row
/// states so the grid feels consistent.
/// </summary>
public sealed partial class ScriptItem : ObservableObject
{
    [ObservableProperty] private bool   _isSelected;
    [ObservableProperty] private int    _index;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private BatchStatus _status  = BatchStatus.Pending;
    [ObservableProperty] private string      _message = "";

    public ScriptItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }
}

/// <summary>
/// File-driven companion to Batch. The user picks .sql files (one,
/// many, or a whole folder, or via drag-drop), ticks one or more
/// targets, and clicks Execute — every script runs against every
/// ticked target via <see cref="SqlScriptRunner"/>, which honours
/// <c>GO</c> batch terminators.
///
/// Use case: revert a batch sync by executing the previously-captured
/// backup .sql files against the targets that drifted.
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly AppServices _svc;

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _status = "Drop .sql files / a folder, or pick them, then choose targets and Execute.";
    [ObservableProperty] private int    _successCount;
    [ObservableProperty] private int    _failCount;
    [ObservableProperty] private string _targetFilter = "";

    public ObservableCollection<ScriptItem>      Items           { get; } = new();
    public ObservableCollection<TargetPickVm>    Targets         { get; } = new();
    public ObservableCollection<TargetPickVm>    FilteredTargets { get; } = new();

    public int TargetSelectedCount => Targets.Count(t => t.IsChecked);

    public ScriptsViewModel(AppServices svc)
    {
        _svc = svc;
        Items.CollectionChanged += (_, _) => Renumber();
        Reload();
    }

    /// <summary>Re-pull the target list from the active connection group.</summary>
    public void Reload()
    {
        var previouslyChecked = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();
        foreach (var t in Targets) t.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Clear();
        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            var key = $"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}";
            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains(key));
            pick.PropertyChanged += OnTargetPropertyChanged;
            Targets.Add(pick);
        }
        RebuildFilteredTargets();
        OnPropertyChanged(nameof(TargetSelectedCount));
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetPickVm.IsChecked))
            OnPropertyChanged(nameof(TargetSelectedCount));
    }

    partial void OnTargetFilterChanged(string value) => RebuildFilteredTargets();

    private void RebuildFilteredTargets()
    {
        FilteredTargets.Clear();
        var f = (TargetFilter ?? "").Trim();
        foreach (var t in Targets)
        {
            if (f.Length == 0 ||
                t.Label.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                t.Environment.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                t.Database.Contains(f, StringComparison.OrdinalIgnoreCase))
                FilteredTargets.Add(t);
        }
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

    /// <summary>
    /// Add file paths to the list, deduping by absolute path so a
    /// re-drop / repeat-pick doesn't double the rows. Non-.sql paths
    /// are ignored silently. Returns the number actually added.
    /// </summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Items.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            // Folder → recurse for .sql files; file → take if .sql.
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*.sql", SearchOption.AllDirectories))
                {
                    if (existing.Add(f))
                    {
                        Items.Add(new ScriptItem(f));
                        added++;
                    }
                }
            }
            else if (File.Exists(p) && p.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Add(p))
                {
                    Items.Add(new ScriptItem(p));
                    added++;
                }
            }
        }
        if (added > 0)
        {
            Status = $"Added {added} script file(s). Total: {Items.Count}.";
            _svc.Toasts.Success("Scripts added", $"{added} added · {Items.Count} total.");
        }
        return added;
    }

    [RelayCommand]
    private void Clear()
    {
        if (Items.Count == 0) return;
        var n = Items.Count;
        Items.Clear();
        SuccessCount = FailCount = 0;
        Status = "Cleared.";
        _svc.Toasts.Info("Scripts cleared", $"Removed {n} row(s).");
    }

    [RelayCommand]
    private void RemoveChecked()
    {
        var doomed = Items.Where(i => i.IsSelected).ToList();
        if (doomed.Count == 0)
        {
            _svc.Toasts.Warning("No rows selected", "Tick rows first.");
            return;
        }
        foreach (var d in doomed) Items.Remove(d);
        Status = $"Removed {doomed.Count} row(s). {Items.Count} remaining.";
    }

    /// <summary>
    /// Run every script against every ticked target. Outcomes are
    /// recorded per-row in <see cref="ScriptItem.Message"/>; the
    /// row's <see cref="ScriptItem.Status"/> is the worst-of-all
    /// aggregate (any target failure → Failed). Pre-flight: must have
    /// items + at least one ticked target.
    /// </summary>
    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (Items.Count == 0)
        {
            _svc.Toasts.Warning("Nothing to run", "Add some .sql files first.");
            return;
        }
        var checkedTargets = Targets.Where(t => t.IsChecked).ToList();
        if (checkedTargets.Count == 0)
        {
            _svc.Toasts.Warning("No targets", "Tick one or more targets before executing.");
            return;
        }

        IsBusy = true;
        SuccessCount = FailCount = 0;
        try
        {
            foreach (var item in Items.ToList())
            {
                item.Status  = BatchStatus.Running;
                item.Message = "";
                var msgs = new List<string>();
                int ok = 0, fail = 0;

                foreach (var t in checkedTargets)
                {
                    var conn = _svc.Connections.Get(t.Environment, t.Database);
                    if (string.IsNullOrWhiteSpace(conn))
                    {
                        msgs.Add($"[{t.Environment}·{t.Database}] no connection");
                        fail++;
                        continue;
                    }
                    var outcome = await _svc.Scripts.ExecuteFileAsync(item.FilePath, conn!);
                    if (outcome.Status == ScriptStatus.Success)
                    {
                        msgs.Add($"[{t.Environment}·{t.Database}] {outcome.BatchesExecuted} batch(es)");
                        ok++;
                    }
                    else
                    {
                        msgs.Add($"[{t.Environment}·{t.Database}] {outcome.Error}");
                        fail++;
                    }
                }

                item.Message = string.Join(" | ", msgs);
                if (fail == 0 && ok > 0)
                {
                    item.Status = BatchStatus.Success;
                    SuccessCount++;
                }
                else
                {
                    item.Status = BatchStatus.Failed;
                    FailCount++;
                }
            }

            Status = $"Done. OK: {SuccessCount} · Fail: {FailCount}.";
            if (FailCount == 0 && SuccessCount > 0)
                _svc.Toasts.Success("Scripts complete", $"OK: {SuccessCount}");
            else if (SuccessCount > 0 && FailCount > 0)
                _svc.Toasts.Warning("Scripts finished with errors", $"OK: {SuccessCount} · Fail: {FailCount}");
            else
                _svc.Toasts.Error("Scripts failed", $"Fail: {FailCount}");
        }
        finally { IsBusy = false; }
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].Index = i + 1;
    }
}
