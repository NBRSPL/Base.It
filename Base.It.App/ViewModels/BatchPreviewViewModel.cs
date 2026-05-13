using System.Collections.ObjectModel;
using Base.It.App.Services;
using Base.It.Core.Diff;
using Base.It.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Base.It.App.ViewModels;

/// <summary>
/// One endpoint feeding the Batch Preview window: a label, an optional
/// colour, and the connection string used to fetch its definition.
/// </summary>
public sealed record PreviewEndpoint(string Label, string? Color, string ConnectionString);

/// <summary>
/// Source-and-targets preview for a single Batch row. Fetches the object's
/// CREATE definition from every endpoint (source + each ticked target),
/// aligns them line-by-line via <see cref="LineAligner"/>, and exposes the
/// resulting <see cref="EnvPane"/> collection — the same shape Compare
/// uses, so the same renderer (with diff highlighting) works here.
///
/// Endpoints whose object isn't found, or whose connection failed, end
/// up with a non-empty <see cref="LoadError"/>; the user sees the bad
/// endpoint listed in the error block instead of silently disappearing
/// from the panes list.
/// </summary>
public sealed partial class BatchPreviewViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly string _objectName;
    private readonly IReadOnlyList<PreviewEndpoint> _endpoints;

    /// <summary>
    /// Optional literal "source" definition used by the Scripts pane: when
    /// the source is a file on disk (not a database), we already have the
    /// CREATE text and don't need to fetch anything. The first pane is
    /// built from this string; the rest are still fetched from
    /// <see cref="_endpoints"/> via <see cref="_objectName"/>. Null = normal
    /// mode where every pane is fetched.
    /// </summary>
    private readonly string? _sourceOverrideDefinition;
    private readonly string? _sourceOverrideLabel;
    private readonly string? _sourceOverrideColor;

    public string Title { get; }
    public ObservableCollection<EnvPane> Panes { get; } = new();

    [ObservableProperty] private string _status = "Fetching definitions…";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _loadError = "";

    public BatchPreviewViewModel(AppServices svc, string objectName, IReadOnlyList<PreviewEndpoint> endpoints)
    {
        _svc = svc;
        _objectName = objectName;
        _endpoints = endpoints;
        Title = $"Preview: {objectName}";
    }

    /// <summary>
    /// Build a preview where the "source" comes from a literal string (a
    /// .sql file on disk) instead of a database fetch. Targets still come
    /// from <paramref name="targets"/> and are fetched using the detected
    /// object name; when the file doesn't reference a recognisable object,
    /// target panes will fail to find anything and the user still sees the
    /// source content side-by-side with the failures listed.
    /// </summary>
    public static BatchPreviewViewModel ForFileAndTargets(
        AppServices svc,
        string sourceLabel,
        string fileContent,
        string? objectName,
        IReadOnlyList<PreviewEndpoint> targets)
    {
        var vm = new BatchPreviewViewModel(
            svc,
            objectName ?? "(script)",
            targets,
            sourceOverrideDefinition: fileContent,
            sourceOverrideLabel:      sourceLabel,
            sourceOverrideColor:      null);
        return vm;
    }

    private BatchPreviewViewModel(
        AppServices svc,
        string objectName,
        IReadOnlyList<PreviewEndpoint> endpoints,
        string sourceOverrideDefinition,
        string sourceOverrideLabel,
        string? sourceOverrideColor)
    {
        _svc                       = svc;
        _objectName                = objectName;
        _endpoints                 = endpoints;
        _sourceOverrideDefinition  = sourceOverrideDefinition;
        _sourceOverrideLabel       = sourceOverrideLabel;
        _sourceOverrideColor       = sourceOverrideColor;
        Title = $"Preview: {sourceOverrideLabel}";
    }

    /// <summary>
    /// Pull every endpoint's definition, then build aligned panes against
    /// each peer. Mirrors <see cref="CompareTabViewModel.LoadAsync"/>'s
    /// flow so the same diff highlights apply: a line is "Different"
    /// only when no peer endpoint has the same line.
    /// </summary>
    internal async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Fetching definitions…";
        Panes.Clear();
        LoadError = "";

        try
        {
            var collected = new List<(string Label, string? Color, string? Definition, string? Error)>();

            // Script-file mode: seed the first pane from the literal source
            // text instead of fetching. Targets are still looked up below
            // via the (possibly-detected) object name.
            if (_sourceOverrideDefinition is not null)
            {
                collected.Add((_sourceOverrideLabel ?? "Source", _sourceOverrideColor, _sourceOverrideDefinition, null));
            }

            // Only try to parse the object name when it's actually meaningful.
            // For script previews where we couldn't detect an object, _objectName
            // is "(script)" and we skip target fetches entirely.
            ObjectIdentifier? id = null;
            if (!string.IsNullOrWhiteSpace(_objectName) && !_objectName.StartsWith("("))
            {
                try { id = ObjectIdentifier.Parse(_objectName); } catch { id = null; }
            }

            foreach (var ep in _endpoints)
            {
                if (string.IsNullOrWhiteSpace(ep.ConnectionString))
                {
                    collected.Add((ep.Label, ep.Color, null, "no connection string"));
                    continue;
                }
                if (id is null)
                {
                    collected.Add((ep.Label, ep.Color, null, "script doesn't reference a known object — nothing to fetch"));
                    continue;
                }

                try
                {
                    var obj = await _svc.Scripter.GetObjectAsync(ep.ConnectionString, id.Value);
                    collected.Add((ep.Label, ep.Color, obj?.Definition, obj is null ? "not found" : null));
                }
                catch (Exception ex)
                {
                    collected.Add((ep.Label, ep.Color, null, ex.InnerException?.Message ?? ex.Message));
                }
            }

            var withContent = collected
                .Where(x => !string.IsNullOrWhiteSpace(x.Definition))
                .ToList();

            if (withContent.Count == 0)
            {
                Status = $"'{_objectName}' not found in any endpoint.";
                return;
            }

            var allDefs = withContent.Select(x => x.Definition!).ToList();
            foreach (var (label, color, def, _) in withContent)
            {
                var peers = allDefs.Where(d => !ReferenceEquals(d, def));
                var lines = LineAligner.Align(def!, peers);
                Panes.Add(new EnvPane(label, color, def!, lines));
            }

            // Surface failures in a neutral block above the panes so the
            // user sees "PROD/Customers — connection refused" rather than
            // silently missing pane.
            var failures = collected
                .Where(x => string.IsNullOrWhiteSpace(x.Definition))
                .Select(x => $"  • {x.Label}: {x.Error ?? "no definition"}")
                .ToList();
            LoadError = failures.Count == 0
                ? ""
                : "Some endpoints couldn't be loaded:\n" + string.Join('\n', failures);

            var diffs = Panes.Sum(p => p.Lines.Count(l => l.State == LineState.Different));
            Status = diffs == 0
                ? $"All {Panes.Count} endpoint(s) match — no differences."
                : $"{Panes.Count} endpoint(s), {diffs} differing line(s).";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
