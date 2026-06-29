using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Base.It.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Ad-hoc T-SQL runner. Pick one or more (env, database) targets via the
/// same "To" picker used by Batch / Sync / Scripts, type T-SQL, Run —
/// every ticked target executes the query and the results are listed
/// per-target.
/// </summary>
public sealed partial class QueryViewModel : ObservableObject, ICsvExportable
{
    private readonly AppServices _svc;

    [ObservableProperty] private string _query   = "";
    [ObservableProperty] private string _results = "";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _status  = "Idle.";

    /// <summary>Exposed so the View's Export handler can fire the result toast.</summary>
    public ToastService Toasts => _svc.Toasts;

    // Structured capture of the most recent run's result-set rows, so the
    // results — shown as text in the pane — can still be exported as proper
    // CSV. A leading "Target" column distinguishes rows across targets.
    private List<string> _exportColumns = new() { "Target" };
    private readonly List<string?[]> _exportRows = new();

    public ObservableCollection<TargetPickVm>  Targets         { get; } = new();
    public ObservableCollection<EndpointPick>  Endpoints       { get; } = new();
    public ObservableCollection<EndpointPick>  TargetCandidateEndpoints { get; } = new();
    public ObservableCollection<TargetPickVm>  CheckedTargets         { get; } = new();
    public ObservableCollection<TargetPickVm>  CheckedTargetsVisible  { get; } = new();
    public ObservableCollection<TargetPickVm>  CheckedTargetsOverflow { get; } = new();

    private const int VisibleTargetChipsMax = 3;

    public int  CheckedTargetsOverflowCount => CheckedTargetsOverflow.Count;
    public bool HasCheckedTargetsOverflow   => CheckedTargetsOverflow.Count > 0;
    public int  TargetSelectedCount         => Targets.Count(t => t.IsChecked);

    [ObservableProperty] private EndpointPick? _nextTargetEndpoint;

    public QueryViewModel(AppServices svc)
    {
        _svc = svc;
        Reload();
    }

    public void Reload()
    {
        var previouslyChecked = Targets.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();
        foreach (var t in Targets) t.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Clear();
        CheckedTargets.Clear();

        Endpoints.Clear();
        foreach (var ep in EnvironmentListProvider.Endpoints(_svc)) Endpoints.Add(ep);

        foreach (var cfg in EnvironmentListProvider.VisibleConnections(_svc))
        {
            var key = $"{cfg.Environment?.ToUpperInvariant()}|{cfg.Database?.ToUpperInvariant()}";
            var pick = TargetPickVm.From(_svc, cfg.Environment, cfg.Database,
                isChecked: previouslyChecked.Contains(key));
            pick.PropertyChanged += OnTargetPropertyChanged;
            Targets.Add(pick);
            if (pick.IsChecked) CheckedTargets.Add(pick);
        }
        RebuildEndpointCandidates();
        RebuildCheckedTargetSlices();
        OnPropertyChanged(nameof(TargetSelectedCount));
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TargetPickVm.IsChecked)) return;
        OnPropertyChanged(nameof(TargetSelectedCount));

        if (sender is TargetPickVm vm)
        {
            if (vm.IsChecked && !CheckedTargets.Contains(vm))
                CheckedTargets.Add(vm);
            else if (!vm.IsChecked)
                CheckedTargets.Remove(vm);
        }

        RebuildEndpointCandidates();
        RebuildCheckedTargetSlices();
    }

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

    private void RebuildEndpointCandidates()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            RebuildEndpointCandidatesCore,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RebuildEndpointCandidatesCore()
    {
        bool MatchesAnyTicked(EndpointPick ep) =>
            CheckedTargets.Any(t =>
                string.Equals(t.Environment, ep.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Database,    ep.Database,    StringComparison.OrdinalIgnoreCase));

        TargetCandidateEndpoints.Clear();
        foreach (var ep in Endpoints)
            if (!MatchesAnyTicked(ep)) TargetCandidateEndpoints.Add(ep);
    }

    partial void OnNextTargetEndpointChanged(EndpointPick? value)
    {
        if (value is null) return;
        var t = Targets.FirstOrDefault(t =>
            string.Equals(t.Environment, value.Environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Database,    value.Database,    StringComparison.OrdinalIgnoreCase));
        if (t is not null && !t.IsChecked) t.IsChecked = true;
        NextTargetEndpoint = null;
    }

    public void UncheckTarget(TargetPickVm t)
    {
        if (t is null) return;
        t.IsChecked = false;
    }

    [RelayCommand]
    private void ClearTargets()
    {
        foreach (var t in Targets) t.IsChecked = false;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) { Status = "Enter a query."; return; }
        var checkedTargets = Targets.Where(t => t.IsChecked).ToList();
        if (checkedTargets.Count == 0) { Status = "Pick at least one target."; return; }

        IsBusy = true; Status = "Running..."; Results = "";
        var sb = new StringBuilder();
        _exportColumns = new List<string> { "Target" };
        _exportRows.Clear();
        try
        {
            foreach (var t in checkedTargets)
            {
                var targetLabel = $"{t.Environment} / {t.Database}";
                sb.AppendLine($"=== [{targetLabel}] ===");
                var conn = _svc.Connections.Get(t.Environment, t.Database);
                if (string.IsNullOrWhiteSpace(conn)) { sb.AppendLine("No connection string configured.\n"); continue; }

                var outcome = await _svc.Query.ExecuteAsync(conn!, Query);
                if (outcome.Error is not null) { sb.AppendLine(outcome.Error); }
                else if (outcome.IsResultSet && outcome.Rows is { } rows)
                {
                    sb.AppendLine($"{rows.Columns.Count} col, {rows.Rows.Count} row(s).");
                    var colNames = rows.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToList();
                    sb.AppendLine(string.Join(" | ", colNames));
                    foreach (System.Data.DataRow row in rows.Rows)
                        sb.AppendLine(string.Join(" | ", row.ItemArray.Select(x => x?.ToString() ?? "NULL")));

                    CaptureForExport(targetLabel, colNames, rows);
                }
                else { sb.AppendLine($"Rows affected: {outcome.RowsAffected}"); }
                sb.AppendLine();
            }
            Results = sb.ToString();
            Status = $"Ran against {checkedTargets.Count} target(s).";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally              { IsBusy = false; }
    }

    // ───────────────────────── CSV export ──────────────────────────
    // The results pane is a per-target text dump (it can hold several
    // result sets at once), so it isn't a single sortable grid. Export
    // captures the result-set rows into a flat table with a leading
    // "Target" column. Data columns come from the first result set; the
    // common case (same query across same-schema targets) lines up exactly.

    private void CaptureForExport(string target, List<string> colNames, System.Data.DataTable rows)
    {
        if (_exportColumns.Count == 1) // only the leading "Target" so far
            _exportColumns.AddRange(colNames);

        foreach (System.Data.DataRow row in rows.Rows)
        {
            var cells = new string?[_exportColumns.Count];
            cells[0] = target;
            var items = row.ItemArray;
            for (int i = 0; i < items.Length && i + 1 < cells.Length; i++)
                cells[i + 1] = items[i]?.ToString() ?? "NULL";
            _exportRows.Add(cells);
        }
    }

    public string CsvSuggestedFileName => "query-results.csv";
    public IReadOnlyList<string> CsvHeaders => _exportColumns;
    public bool HasExportableRows => _exportRows.Count > 0;
    public IEnumerable<IReadOnlyList<string?>> CsvRows() => _exportRows;
}
