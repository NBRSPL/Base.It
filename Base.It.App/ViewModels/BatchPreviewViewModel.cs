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
    /// When true the diff ignores spaces and tabs — lines that differ only in
    /// indentation / spacing show as in-sync. Toggling re-aligns the already
    /// loaded definitions (no refetch). Bound to the "Ignore spaces &amp; tabs"
    /// checkbox in the preview.
    /// </summary>
    // Seeded from the persisted preference so the choice survives closing
    // a preview, opening another, and app restarts.
    [ObservableProperty] private bool _ignoreWhitespace = Services.DiffViewPrefs.IgnoreWhitespace;

    /// <summary>Formatted definitions captured at load, so the whitespace
    /// toggle can re-align without hitting the database again.</summary>
    private List<(string Label, string? Color, string Definition)>? _loadedDefs;

    /// <summary>Endpoint-failure text to re-append to LoadError on every
    /// rebuild (fetch failures survive a whitespace-toggle re-align).</summary>
    private string _fetchFailureBlock = "";

    // Guards the auto-seed path (see MaybeAutoEnableIgnoreWhitespace): when
    // set, flipping IgnoreWhitespace neither persists the global default nor
    // rebuilds the panes (LoadAsync rebuilds right after), but the property
    // change still notifies the checkbox binding.
    private bool _seedingIgnoreWhitespace;

    partial void OnIgnoreWhitespaceChanged(bool value)
    {
        if (_seedingIgnoreWhitespace) return;
        Services.DiffViewPrefs.IgnoreWhitespace = value; // persist across previews + restarts
        BuildPanes();
    }

    /// <summary>
    /// When true, <see cref="LoadAsync"/> auto-ticks <see cref="IgnoreWhitespace"/>
    /// if the loaded definitions match ONLY after whitespace is ignored — i.e.
    /// the object is "in sync" purely because of formatting (the same rule the
    /// Batch ✓ tick uses). This makes previewing an in-sync-by-whitespace row
    /// open with "Ignore spaces &amp; tabs" already ticked, so the preview
    /// agrees with the tick. If the sides are byte-identical (in sync without
    /// needing whitespace removal), the checkbox is left at its usual default.
    /// Not persisted — affects only this preview instance. Set by the Batch row
    /// preview builder; left off for snapshot / script previews.
    /// </summary>
    public bool AutoIgnoreWhitespaceForInSync { get; set; }

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
        // Format both sides identically before diffing (see SqlFormatter) so
        // the highlight shows real changes, not cosmetic whitespace/casing noise.
        leftDefinition  = Base.It.Core.Parsing.SqlFormatter.Format(leftDefinition);
        rightDefinition = Base.It.Core.Parsing.SqlFormatter.Format(rightDefinition);

        var vm = new BatchPreviewViewModel(svc, title, Array.Empty<PreviewEndpoint>());
        vm.Title = title;
        // Critical: tell LoadAsync to bail. BatchPreviewWindow auto-fires
        // LoadAsync on Opened, and without this flag it would wipe these
        // pre-built panes and replace them with "not found in the
        // endpoint" errors (we have no endpoints to query in this mode).
        vm._skipFetchOnLoad = true;

        vm._loadedDefs = new()
        {
            (leftLabel,  leftColor,  leftDefinition),
            (rightLabel, rightColor, rightDefinition),
        };
        vm.BuildPanes();
        return vm;
    }

    /// <summary>
    /// If <see cref="AutoIgnoreWhitespaceForInSync"/> is set, turn on
    /// <see cref="IgnoreWhitespace"/> when the loaded definitions differ in
    /// text but are equal under the whitespace-insensitive hash — i.e. the
    /// only differences are spaces / tabs / formatting, exactly the case the
    /// Batch ✓ tick calls "in sync". Byte-identical sides are left untouched
    /// (nothing to ignore); genuinely different sides are left untouched
    /// (there's a real change to show). Sets the backing field directly so it
    /// does NOT write to the persisted DiffViewPrefs default — this is a
    /// per-preview nudge, not a preference change.
    /// </summary>
    private void MaybeAutoEnableIgnoreWhitespace()
    {
        if (!AutoIgnoreWhitespaceForInSync) return;
        if (IgnoreWhitespace) return;                 // already on (user pref) — nothing to do
        var defs = _loadedDefs;
        if (defs is not { Count: >= 2 }) return;

        var first = defs[0].Definition;
        // Byte-identical already → "normally that way", leave the checkbox as-is.
        if (defs.All(d => string.Equals(d.Definition, first, StringComparison.Ordinal))) return;

        // Match ONLY after whitespace removal? Use the same whitespace-
        // insensitive, literal-preserving hash the tick uses, so a real
        // difference inside a string literal ('a b' vs 'a  b') does NOT
        // trip this.
        var firstHash = Base.It.Core.Hashing.DefinitionHasher.Hash(first);
        bool hashEqual = defs.All(d => string.Equals(
            Base.It.Core.Hashing.DefinitionHasher.Hash(d.Definition), firstHash, StringComparison.Ordinal));

        if (hashEqual)
        {
            // Flip via the property (so the checkbox binding updates) but under
            // the seed guard so it doesn't persist the global default; LoadAsync
            // rebuilds the panes with this value immediately after.
            _seedingIgnoreWhitespace = true;
            try { IgnoreWhitespace = true; }
            finally { _seedingIgnoreWhitespace = false; }
        }
    }

    /// <summary>
    /// (Re)build the panes from <see cref="_loadedDefs"/> honouring
    /// <see cref="IgnoreWhitespace"/>. Called after a load and again whenever
    /// the whitespace toggle flips — no refetch needed. Two definitions use
    /// the pair-aware char-diff aligner; three or more fall back to N-way
    /// Align. A failure in the char-refinement drops to line-level Align so
    /// the panes still render.
    /// </summary>
    private void BuildPanes()
    {
        Panes.Clear();
        LoadError = "";

        var defs = _loadedDefs;
        if (defs is { Count: > 0 })
        {
            try
            {
                if (defs.Count == 2)
                {
                    var (aLines, bLines) = LineAligner.AlignPair(defs[0].Definition, defs[1].Definition, IgnoreWhitespace);
                    Panes.Add(new EnvPane(defs[0].Label, defs[0].Color, defs[0].Definition, aLines));
                    Panes.Add(new EnvPane(defs[1].Label, defs[1].Color, defs[1].Definition, bLines));
                }
                else
                {
                    // N-way (3+): render is base-relative (pane 0 = base), so the
                    // per-column badges must be too — a block that exists in only
                    // one target is a real change even though the base is unchanged.
                    var all = defs.Select(d => d.Definition).ToList();
                    var badges = MultiDiffStats.Compute(all, IgnoreWhitespace);
                    for (int i = 0; i < defs.Count; i++)
                    {
                        var d = defs[i];
                        var peers = all.Where(x => !ReferenceEquals(x, d.Definition));
                        Panes.Add(new EnvPane(d.Label, d.Color, d.Definition,
                            LineAligner.Align(d.Definition, peers, IgnoreWhitespace))
                        {
                            DiffBadge = i < badges.Count ? badges[i] : null,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Panes.Clear();
                var all = defs.Select(d => d.Definition).ToList();
                foreach (var d in defs)
                {
                    var peers = all.Where(x => !ReferenceEquals(x, d.Definition));
                    Panes.Add(new EnvPane(d.Label, d.Color, d.Definition,
                        LineAligner.Align(d.Definition, peers, IgnoreWhitespace)));
                }
                var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                LoadError = $"Char-level diff fell back to line-level — {ex.GetType().Name}: {ex.Message}\n{firstFrame}";
            }
        }

        if (!string.IsNullOrEmpty(_fetchFailureBlock))
            LoadError = string.IsNullOrEmpty(LoadError)
                ? _fetchFailureBlock
                : LoadError + "\n\n" + _fetchFailureBlock;

        if (Panes.Count >= 3)
        {
            // N-way: the change lives in whichever target differs from the base,
            // NOT necessarily in the base pane — so count differing environments
            // from the per-column badges (which are base-relative), not the base
            // pane's own changed-line count (that would read "No changes" whenever
            // the base itself is untouched).
            int differing = Panes.Skip(1).Count(p => p.DiffBadge?.Differs == true);
            Status = differing == 0
                ? "No changes."
                : differing == 1 ? "1 environment differs." : $"{differing} environments differ.";
        }
        else
        {
            var changes = Panes.FirstOrDefault()?.Lines
                .Count(l => l.State == LineState.Different) ?? 0;
            Status = changes == 0
                ? "No changes."
                : changes == 1 ? "1 change" : $"{changes} changes";
        }
        RebuildChangeIndex();
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
                // Surface per-endpoint reason instead of a blanket "not found."
                // The old message hid whether one endpoint had a real error
                // and another was just missing the object — you couldn't tell.
                var lines = collected
                    .Select(x => $"  • {x.Label}: {(string.IsNullOrWhiteSpace(x.Error) ? "not found" : x.Error)}");
                Status = $"'{_objectName}' not found in any endpoint:\n" + string.Join("\n", lines);
                return;
            }

            // Format every side identically before diffing (see SqlFormatter)
            // so the highlight reflects real changes, not cosmetic whitespace /
            // casing / line-break differences. Best-effort: anything ScriptDom
            // can't parse is echoed back unchanged.
            _loadedDefs = withContent
                .Select(x => (x.Label, x.Color, Base.It.Core.Parsing.SqlFormatter.Format(x.Definition)))
                .ToList();

            // Surface fetch failures in a neutral block above the panes so the
            // user sees "PROD/Customers — connection refused" rather than a
            // silently missing pane. Stored so it survives whitespace re-aligns.
            var failures = collected
                .Where(x => string.IsNullOrWhiteSpace(x.Definition))
                .Select(x => $"  • {x.Label}: {x.Error ?? "no definition"}")
                .ToList();
            _fetchFailureBlock = failures.Count > 0
                ? "Some endpoints couldn't be loaded:\n" + string.Join('\n', failures)
                : "";

            // If this preview was opened for an in-sync (✓) row and the sides
            // match only after whitespace is ignored, pre-tick the toggle so
            // the preview reflects the same verdict as the tick.
            MaybeAutoEnableIgnoreWhitespace();

            // Pair-aware char-diff for two panes, N-way Align for more —
            // honouring the whitespace toggle. (See BuildPanes.)
            BuildPanes();
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
