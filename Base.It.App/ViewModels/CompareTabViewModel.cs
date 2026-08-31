using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Base.It.App.Services;
using Base.It.Core.Config;
using Base.It.Core.Diff;
using Base.It.Core.Models;
using Base.It.Core.Parsing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// One Compare tab: fetches a single object across configured environments and
/// exposes the aligned-line panes plus the shared vertical scroll offset.
/// </summary>
public sealed partial class CompareTabViewModel : ObservableObject, ICsvExportable
{
    private readonly AppServices _svc;

    /// <summary>Exposed so the View's Export handler can fire the result toast.</summary>
    public ToastService Toasts => _svc.Toasts;

    public string ObjectName { get; }
    public string Database   { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string _status = "Fetching...";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private EnvPane? _expandedPane;
    [ObservableProperty] private Vector _sharedScrollOffset = new(0, 0);

    /// <summary>
    /// When true the diff ignores spaces and tabs — lines that differ only in
    /// indentation / spacing show as in-sync. Toggling re-aligns the already
    /// fetched definitions (no refetch). Bound to the "Ignore spaces &amp; tabs"
    /// checkbox above the Compare panes.
    /// </summary>
    // Seeded from the persisted preference so the choice survives across
    // Compare tabs and app restarts (shared with the Batch/Sync preview).
    [ObservableProperty] private bool _ignoreWhitespace = Services.DiffViewPrefs.IgnoreWhitespace;

    /// <summary>Formatted definitions captured at load so the whitespace
    /// toggle can re-align without hitting the database again.</summary>
    private List<(string Label, string? Color, string Definition)>? _loadedDefs;

    partial void OnIgnoreWhitespaceChanged(bool value)
    {
        Services.DiffViewPrefs.IgnoreWhitespace = value; // persist across tabs + restarts
        BuildPanes();
    }

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

            // Pretty-print every side the same way *before* diffing so the
            // highlight reflects real changes, not cosmetic whitespace/casing
            // differences. Best-effort: any definition ScriptDom can't parse
            // falls back to its raw text (Format echoes the input). Captured so
            // the whitespace toggle can re-align without refetching.
            _loadedDefs = withContent
                .Select(x => (x.Profile.Label, (string?)x.Profile.Color, SqlFormatter.Format(x.Definition!)))
                .ToList();
            BuildPanes();

            var missing = collected.Count - withContent.Count;
            Status = missing == 0
                ? $"{ObjectName} — {withContent.Count} env(s)."
                : $"{ObjectName} — {withContent.Count} env(s), missing in {missing}.";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally               { IsBusy = false; }
    }

    /// <summary>
    /// (Re)build the panes from <see cref="_loadedDefs"/> honouring
    /// <see cref="IgnoreWhitespace"/>. Called after a fetch and again whenever
    /// the whitespace toggle flips — no refetch needed.
    /// </summary>
    private void BuildPanes()
    {
        var defs = _loadedDefs;
        if (defs is null) return;

        var expandedLabel = ExpandedPane?.Label;
        Panes.Clear();

        // Two environments → the git-quality pair aligner (patience anchoring
        // + similarity pairing + char-level segments) so the common
        // "compare two objects" case gets precise, VS-Code-style highlights.
        // Three or more → N-way membership marking (no single pair to
        // char-diff against), same as before.
        if (defs.Count == 2)
        {
            var (aLines, bLines) = LineAligner.AlignPair(defs[0].Definition, defs[1].Definition, IgnoreWhitespace);
            Panes.Add(new EnvPane(defs[0].Label, defs[0].Color, defs[0].Definition, aLines));
            Panes.Add(new EnvPane(defs[1].Label, defs[1].Color, defs[1].Definition, bLines));
        }
        else
        {
            // N-way (3+): render is base-relative (pane 0 = base), so the per-column
            // badges are too — a block that exists in only one environment is a real
            // change even when the base is unchanged.
            var all = defs.Select(d => d.Definition).ToList();
            var badges = MultiDiffStats.Compute(all, IgnoreWhitespace);
            for (int i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                var peers = all.Where(x => !ReferenceEquals(x, d.Definition));
                var lines = LineAligner.Align(d.Definition, peers, IgnoreWhitespace);
                Panes.Add(new EnvPane(d.Label, d.Color, d.Definition, lines)
                {
                    DiffBadge = i < badges.Count ? badges[i] : null,
                });
            }
        }

        // Preserve an active expand-to-one-pane selection across a re-align.
        ExpandedPane = expandedLabel is null ? null : Panes.FirstOrDefault(p => p.Label == expandedLabel);
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

    // ───────────────────────── CSV export ──────────────────────────
    // The diff itself can't be sorted (that would destroy the line
    // alignment), but the side-by-side comparison maps cleanly to a
    // table: a Line column plus one column per environment pane.

    public string CsvSuggestedFileName => $"compare-{Label}.csv";

    public IReadOnlyList<string> CsvHeaders =>
        new[] { "Line" }.Concat(Panes.Select(p => p.Label)).ToList();

    public bool HasExportableRows => Panes.Count > 0 && Panes.Any(p => p.Lines.Count > 0);

    public IEnumerable<IReadOnlyList<string?>> CsvRows()
    {
        var maxLines = Panes.Count == 0 ? 0 : Panes.Max(p => p.Lines.Count);
        for (int i = 0; i < maxLines; i++)
        {
            var cells = new string?[Panes.Count + 1];
            cells[0] = (i + 1).ToString();
            for (int p = 0; p < Panes.Count; p++)
                cells[p + 1] = i < Panes[p].Lines.Count ? Panes[p].Lines[i].Text : "";
            yield return cells;
        }
    }
}
