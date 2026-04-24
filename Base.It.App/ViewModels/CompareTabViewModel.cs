using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Base.It.App.Services;
using Base.It.Core.Config;
using Base.It.Core.Diff;
using Base.It.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// One Compare tab: fetches a single object across configured environments and
/// exposes the aligned-line panes plus the shared vertical scroll offset.
/// </summary>
public sealed partial class CompareTabViewModel : ObservableObject
{
    private readonly AppServices _svc;

    public string ObjectName { get; }
    public string Database   { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string _status = "Fetching...";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private EnvPane? _expandedPane;
    [ObservableProperty] private Vector _sharedScrollOffset = new(0, 0);

    public ObservableCollection<EnvPane> Panes { get; } = new();
    public ObservableCollection<EnvironmentConfig> InvolvedConnections { get; } = new();

    public CompareTabViewModel(AppServices svc, string objectName, string database)
    {
        _svc = svc;
        ObjectName = objectName;
        Database   = database;
        _label     = ShortLabel(objectName);
    }

    internal async Task LoadAsync()
    {
        IsBusy = true; Status = "Fetching...";
        Panes.Clear();
        InvolvedConnections.Clear();
        ExpandedPane = null;

        try
        {
            var id = ObjectIdentifier.Parse(ObjectName);
            var collected = new List<(EnvironmentConfig Profile, string? Definition)>();

            foreach (var env in EnvironmentListProvider.Environments(_svc))
            {
                var profile = _svc.Connections.GetProfile(env, Database);
                if (profile is null) continue;
                InvolvedConnections.Add(profile);

                var conn = profile.BuildConnectionString();
                if (string.IsNullOrWhiteSpace(conn)) { collected.Add((profile, null)); continue; }

                var obj = await _svc.Scripter.GetObjectAsync(conn, id);
                collected.Add((profile, obj?.Definition));
            }

            var withContent = collected
                .Where(x => !string.IsNullOrWhiteSpace(x.Definition))
                .ToList();

            if (withContent.Count == 0)
            {
                Status = $"'{id}' not found in any configured environment.";
                return;
            }

            var allDefs = withContent.Select(x => x.Definition!).ToList();
            foreach (var (profile, def) in withContent)
            {
                var peers = allDefs.Where(d => !ReferenceEquals(d, def));
                var lines = LineAligner.Align(def!, peers);
                Panes.Add(new EnvPane(profile.Label, profile.Color, def!, lines));
            }

            var missing = collected.Count - withContent.Count;
            Status = missing == 0
                ? $"{ObjectName} — {withContent.Count} env(s)."
                : $"{ObjectName} — {withContent.Count} env(s), missing in {missing}.";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally               { IsBusy = false; }
    }

    [RelayCommand] private void Expand(EnvPane? pane) => ExpandedPane = pane;
    [RelayCommand] private void Restore() => ExpandedPane = null;

    [RelayCommand]
    private async Task CopyAll(EnvPane? pane)
    {
        if (pane is null) return;
        var cb = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (cb is null) return;
        await cb.SetTextAsync(pane.Definition);
        Status = $"Copied '{pane.Label}' definition ({pane.Definition.Length:N0} chars).";
    }

    private static string ShortLabel(string obj)
    {
        var i = obj.LastIndexOf('.');
        return i >= 0 && i < obj.Length - 1 ? obj[(i + 1)..] : obj;
    }
}
