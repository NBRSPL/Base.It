using System.Collections.ObjectModel;
using Base.It.App.Services;
using Base.It.Core.Diff;
using Base.It.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    /// <summary>
    /// True when every pane is already populated literally (e.g. via
    /// <see cref="ForLiteralPair"/> from a snapshot diff) — no network
    /// fetch is needed and <see cref="LoadAsync"/> bails out at the top
    /// so it doesn't wipe the pre-built panes and replace them with
    /// "not found in the endpoint" errors.
    /// </summary>
    private bool _skipFetchOnLoad;

    public string Title { get; set; }
    public ObservableCollection<EnvPane> Panes { get; } = new();

    [ObservableProperty] private string _status = "Fetching definitions…";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _loadError = "";

    /// <summary>
    /// Line indices (0-based, in the first pane's Lines list) where a
    /// change starts. Populated by <see cref="LoadAsync"/> /
    /// <see cref="ForLiteralPair"/> after alignment runs. Empty when the
    /// two sides match. Used by the change-navigation buttons in the
    /// preview window — Next/Prev advance through this list.
    /// </summary>
    public IReadOnlyList<int> ChangeLineIndices { get; private set; } = Array.Empty<int>();

    /// <summary>
    /// Index INTO <see cref="ChangeLineIndices"/> the user is currently
    /// parked on. <c>-1</c> means "not yet jumped to a change" — the
    /// first Next click moves to index 0 and scrolls. Wraps at the
    /// ends so navigation never dead-ends.
    /// </summary>
    [ObservableProperty] private int _currentChangeIndex = -1;

    /// <summary>"N of M" label for the navigation toolbar.</summary>
    public string ChangeNavigationLabel =>
        ChangeLineIndices.Count == 0
            ? "no changes"
            : CurrentChangeIndex < 0
                ? $"{ChangeLineIndices.Count} change{(ChangeLineIndices.Count == 1 ? "" : "s")}"
                : $"{CurrentChangeIndex + 1} of {ChangeLineIndices.Count}";

    /// <summary>True when there's at least one change to navigate to.</summary>
    public bool HasChanges => ChangeLineIndices.Count > 0;

    /// <summary>
    /// Raised when Next / Prev is clicked. The view subscribes and
    /// scrolls every pane to <see cref="CurrentChangeIndex"/>'s line
    /// number. Kept as an event (not a binding) because scrolling is
    /// imperative — the VM owns the WHERE, the view owns the HOW.
    /// </summary>
    public event Action<int>? ScrollToLineRequested;

    partial void OnCurrentChangeIndexChanged(int value)
        => OnPropertyChanged(nameof(ChangeNavigationLabel));

    /// <summary>
    /// Move the cursor to the next change and ask the view to scroll
    /// there. Wraps to the first change after the last. No-op when the
    /// plan has no changes.
    /// </summary>
    [RelayCommand]
    private void NextChange()
    {
        if (ChangeLineIndices.Count == 0) return;
        CurrentChangeIndex = (CurrentChangeIndex + 1) % ChangeLineIndices.Count;
        ScrollToLineRequested?.Invoke(ChangeLineIndices[CurrentChangeIndex]);
    }

    /// <summary>Move the cursor to the previous change. Wraps to the last from index 0.</summary>
    [RelayCommand]
    private void PrevChange()
    {
        if (ChangeLineIndices.Count == 0) return;
        CurrentChangeIndex = CurrentChangeIndex <= 0
            ? ChangeLineIndices.Count - 1
            : CurrentChangeIndex - 1;
        ScrollToLineRequested?.Invoke(ChangeLineIndices[CurrentChangeIndex]);
    }

    /// <summary>
    /// Recompute <see cref="ChangeLineIndices"/> from the current Panes
    /// list. Picks the first pane that has at least one "Different"
    /// line (typically the source side); change nav is anchored to
    /// that pane and every other pane gets scrolled to the same Y via
    /// <see cref="ScrollToLineRequested"/>. Pure read — does not mutate
    /// Panes. Resets <see cref="CurrentChangeIndex"/> to -1 so the
    /// next Next-click lands on the first change.
    /// </summary>
    private void RebuildChangeIndex()
    {
        var anchor = Panes.FirstOrDefault();
        if (anchor is null) { ChangeLineIndices = Array.Empty<int>(); }
        else
        {
            var idx = new List<int>();
            for (int i = 0; i < anchor.Lines.Count; i++)
                if (anchor.Lines[i].State == LineState.Different)
                    idx.Add(i);
            ChangeLineIndices = idx;
        }
        CurrentChangeIndex = -1;
        OnPropertyChanged(nameof(ChangeNavigationLabel));
        OnPropertyChanged(nameof(HasChanges));
    }

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
    /// Builds a no-fetch preview from two already-known SQL strings —
    /// used by the snapshot Compare grid's per-row eye button. The
    /// stored snapshots already contain the full DACPAC-shaped SQL, so
    /// there's nothing to fetch; we just wrap the strings as panes and
    /// run them through <see cref="LineAligner"/> so the same red-line
    /// diff rendering applies.
    /// </summary>
    public static BatchPreviewViewModel ForLiteralPair(
        AppServices svc,
        string title,
        string leftLabel,  string? leftColor,  string leftDefinition,
        string rightLabel, string? rightColor, string rightDefinition)
    {
        var vm = new BatchPreviewViewModel(svc, title, Array.Empty<PreviewEndpoint>());
        vm.Title = title;
        // Critical: tell LoadAsync to bail. BatchPreviewWindow auto-fires
        // LoadAsync on Opened, and without this flag it would wipe these
        // pre-built panes and replace them with "not found in the
        // endpoint" errors (we have no endpoints to query in this mode).
        vm._skipFetchOnLoad = true;

        // Pair-aware aligner produces per-line char-diff segments so
        // the renderer can highlight only the actual substring change
        // instead of repainting the whole line on a whitespace edit.
        // Two-pane case is the common one (sync screen, snapshot diff,
        // single-target preview); larger fan-outs use the legacy N-way
        // path below in LoadAsync.
        //
        // Same safety net as LoadAsync: any bug inside the char-
        // refinement falls back to line-level Align rather than
        // showing the user an empty / broken pane.
        try
        {
            var (leftLines, rightLines) = Base.It.Core.Diff.LineAligner.AlignPair(leftDefinition, rightDefinition);
            vm.Panes.Add(new EnvPane(leftLabel,  leftColor,  leftDefinition,  leftLines));
            vm.Panes.Add(new EnvPane(rightLabel, rightColor, rightDefinition, rightLines));
        }
        catch (Exception ex)
        {
            vm.Panes.Clear();
            var leftLines  = Base.It.Core.Diff.LineAligner.Align(leftDefinition,  new[] { rightDefinition });
            var rightLines = Base.It.Core.Diff.LineAligner.Align(rightDefinition, new[] { leftDefinition  });
            vm.Panes.Add(new EnvPane(leftLabel,  leftColor,  leftDefinition,  leftLines));
            vm.Panes.Add(new EnvPane(rightLabel, rightColor, rightDefinition, rightLines));
            var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            vm.LoadError = $"Char-level diff fell back to line-level — {ex.GetType().Name}: {ex.Message}\n{firstFrame}";
        }

        var changes = vm.Panes.FirstOrDefault()?.Lines
            .Count(l => l.State == Base.It.Core.Diff.LineState.Different) ?? 0;
        vm.Status = changes == 0
            ? "No changes."
            : changes == 1
                ? "1 change"
                : $"{changes} changes";
        vm.RebuildChangeIndex();
        return vm;
    }

    /// <summary>
    /// Pull every endpoint's definition, then build aligned panes against
    /// each peer. Mirrors <see cref="CompareTabViewModel.LoadAsync"/>'s
    /// flow so the same diff highlights apply: a line is "Different"
    /// only when no peer endpoint has the same line.
    /// </summary>
    internal async Task LoadAsync()
    {
        // No-fetch mode (e.g. snapshot diff via ForLiteralPair): panes are
        // already populated, status is already set. Calling out to
        // endpoints would just blow them away and surface "not found".
        if (_skipFetchOnLoad) return;

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

            // Two-pane case → pair-aware aligner so the renderer paints
            // only the changed substring (whitespace edits no longer
            // blanket a whole line). Three or more panes fall back to
            // the legacy N-way Align because pairwise char-diff against
            // multiple peers would produce conflicting segments.
            //
            // The whole alignment block is wrapped because the char-
            // refinement code is new and ANY arithmetic / index bug in
            // it would otherwise nuke the entire preview. On failure we
            // fall back to the legacy line-only Align — content still
            // appears, just without per-character highlighting — and
            // surface the exception type + first stack frame in
            // LoadError so the bug is debuggable from the screen.
            try
            {
                if (withContent.Count == 2)
                {
                    var a = withContent[0];
                    var b = withContent[1];
                    var (aLines, bLines) = LineAligner.AlignPair(a.Definition!, b.Definition!);
                    Panes.Add(new EnvPane(a.Label, a.Color, a.Definition!, aLines));
                    Panes.Add(new EnvPane(b.Label, b.Color, b.Definition!, bLines));
                }
                else
                {
                    var allDefs = withContent.Select(x => x.Definition!).ToList();
                    foreach (var (label, color, def, _) in withContent)
                    {
                        var peers = allDefs.Where(d => !ReferenceEquals(d, def));
                        var lines = LineAligner.Align(def!, peers);
                        Panes.Add(new EnvPane(label, color, def!, lines));
                    }
                }
            }
            catch (Exception ex)
            {
                Panes.Clear();
                var allDefs = withContent.Select(x => x.Definition!).ToList();
                foreach (var (label, color, def, _) in withContent)
                {
                    var peers = allDefs.Where(d => !ReferenceEquals(d, def));
                    var lines = LineAligner.Align(def!, peers);
                    Panes.Add(new EnvPane(label, color, def!, lines));
                }
                var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                LoadError = $"Char-level diff fell back to line-level — {ex.GetType().Name}: {ex.Message}\n{firstFrame}";
            }

            // Surface failures in a neutral block above the panes so the
            // user sees "PROD/Customers — connection refused" rather than
            // silently missing pane.
            //
            // IMPORTANT: only ADD to LoadError, never CLEAR it. The inner
            // try/catch above may have set LoadError to "Char-level diff
            // fell back to ..." — if no fetch failures, we'd otherwise
            // overwrite that message with "" and the user would have no
            // way to see the exception that broke the diff. The previous
            // version of this line was an unconditional assignment that
            // hid the safety-net diagnostic entirely.
            var failures = collected
                .Where(x => string.IsNullOrWhiteSpace(x.Definition))
                .Select(x => $"  • {x.Label}: {x.Error ?? "no definition"}")
                .ToList();
            if (failures.Count > 0)
            {
                var failBlock = "Some endpoints couldn't be loaded:\n" + string.Join('\n', failures);
                LoadError = string.IsNullOrEmpty(LoadError)
                    ? failBlock
                    : LoadError + "\n\n" + failBlock;
            }

            // The first pane is the anchor for the change count — same
            // value the inline change-nav arrows use. Per-pane "lines
            // changed" counts already show in each pane's header.
            var changes = Panes.FirstOrDefault()?.Lines
                .Count(l => l.State == LineState.Different) ?? 0;
            Status = changes == 0
                ? "No changes."
                : changes == 1
                    ? "1 change"
                    : $"{changes} changes";
            RebuildChangeIndex();
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
