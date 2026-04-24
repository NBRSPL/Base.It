using System.Collections.ObjectModel;
using System.Text;
using Base.It.App.Services;
using Base.It.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

public sealed partial class EnvToggle : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public string Name { get; }
    public EnvironmentConfig? Profile { get; set; }
    public EnvToggle(string name, bool selected = false) { Name = name; _isSelected = selected; }
}

public sealed partial class QueryViewModel : ObservableObject
{
    private readonly AppServices _svc;

    [ObservableProperty] private string? _database;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _results = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Idle.";

    public ObservableCollection<string>            Databases           { get; } = new();
    public ObservableCollection<EnvToggle>         Envs                { get; } = new();
    public ObservableCollection<EnvironmentConfig> InvolvedConnections { get; } = new();

    public QueryViewModel(AppServices svc)
    {
        _svc = svc;
        Reload();
    }

    public void Reload()
    {
        Databases.Clear();
        foreach (var d in EnvironmentListProvider.Databases(_svc)) Databases.Add(d);

        Envs.Clear();
        foreach (var e in EnvironmentListProvider.Environments(_svc))
        {
            var toggle = new EnvToggle(e);
            toggle.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EnvToggle.IsSelected)) RefreshInvolved();
            };
            Envs.Add(toggle);
        }
        Database ??= Databases.FirstOrDefault();
        RefreshInvolved();
    }

    partial void OnDatabaseChanged(string? value) => RefreshInvolved();

    private void RefreshInvolved()
    {
        InvolvedConnections.Clear();
        if (string.IsNullOrWhiteSpace(Database)) return;
        foreach (var env in Envs.Where(e => e.IsSelected))
        {
            var p = _svc.Connections.GetProfile(env.Name, Database!);
            env.Profile = p;
            if (p is not null) InvolvedConnections.Add(p);
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) { Status = "Enter a query."; return; }
        if (string.IsNullOrWhiteSpace(Database)) { Status = "Pick a database."; return; }
        var selected = Envs.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) { Status = "Select at least one environment."; return; }

        IsBusy = true; Status = "Running..."; Results = "";
        var sb = new StringBuilder();
        try
        {
            foreach (var e in selected)
            {
                sb.AppendLine($"=== [{e.Name}] ===");
                var conn = _svc.Connections.Get(e.Name, Database!);
                if (string.IsNullOrWhiteSpace(conn)) { sb.AppendLine("No connection string configured.\n"); continue; }

                var outcome = await _svc.Query.ExecuteAsync(conn!, Query);
                if (outcome.Error is not null) { sb.AppendLine(outcome.Error); }
                else if (outcome.IsResultSet && outcome.Rows is { } rows)
                {
                    sb.AppendLine($"{rows.Columns.Count} col, {rows.Rows.Count} row(s).");
                    sb.AppendLine(string.Join(" | ", rows.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName)));
                    foreach (System.Data.DataRow row in rows.Rows)
                        sb.AppendLine(string.Join(" | ", row.ItemArray.Select(x => x?.ToString() ?? "NULL")));
                }
                else { sb.AppendLine($"Rows affected: {outcome.RowsAffected}"); }
                sb.AppendLine();
            }
            Results = sb.ToString();
            Status = $"Ran against {selected.Count} environment(s).";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally               { IsBusy = false; }
    }
}
