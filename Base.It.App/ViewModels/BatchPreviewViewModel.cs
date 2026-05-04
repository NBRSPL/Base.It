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
            var id = ObjectIdentifier.Parse(_objectName);
            var collected = new List<(PreviewEndpoint Ep, string? Definition, string? Error)>();

            foreach (var ep in _endpoints)
            {
                if (string.IsNullOrWhiteSpace(ep.ConnectionString))
                {
                    collected.Add((ep, null, "no connection string"));
                    continue;
                }

                try
                {
                    var obj = await _svc.Scripter.GetObjectAsync(ep.ConnectionString, id);
                    collected.Add((ep, obj?.Definition, obj is null ? "not found" : null));
                }
                catch (Exception ex)
                {
                    collected.Add((ep, null, ex.InnerException?.Message ?? ex.Message));
                }
            }

            var withContent = collected
                .Where(x => !string.IsNullOrWhiteSpace(x.Definition))
                .ToList();

            if (withContent.Count == 0)
            {
                Status = $"'{id}' not found in any endpoint.";
                return;
            }

            var allDefs = withContent.Select(x => x.Definition!).ToList();
            foreach (var (ep, def, _) in withContent)
            {
                var peers = allDefs.Where(d => !ReferenceEquals(d, def));
                var lines = LineAligner.Align(def!, peers);
                Panes.Add(new EnvPane(ep.Label, ep.Color, def!, lines));
            }

            // Surface failures in a neutral block above the panes so the
            // user sees "PROD/Customers — connection refused" rather than
            // silently missing pane.
            var failures = collected
                .Where(x => string.IsNullOrWhiteSpace(x.Definition))
                .Select(x => $"  • {x.Ep.Label}: {x.Error ?? "no definition"}")
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
