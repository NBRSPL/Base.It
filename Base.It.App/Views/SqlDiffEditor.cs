using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using Base.It.App.ViewModels;
using Base.It.Core.Diff;
using TextMateSharp.Grammars;

namespace Base.It.App.Views;

/// <summary>
/// Git-style side-by-side SQL diff built on the AvaloniaEdit code editor.
/// Two read-only editors (real SQL syntax highlighting via TextMate) are
/// row-aligned — filler blank lines are inserted so a matched / replaced
/// line sits on the same row on both sides, and inserts / deletes leave a
/// blank on the opposite side. Diff highlighting is two-level: a changed
/// line gets a faint full-width wash (a background renderer) and the changed
/// words a stronger wash on top (a colorizing transformer). Line numbers are
/// the ORIGINAL per-side numbers (a custom margin), and vertical scroll is
/// synced across the two editors.
/// </summary>
internal static class SqlDiffEditor
{
    private const string MonoFont = "Cascadia Mono,Consolas,monospace";
    private const double FontSize = 12.5;

    public sealed record Built(
        Control Body, TextEditor Left, TextEditor Right,
        IReadOnlyList<int> LeftLineToRow,
        IReadOnlyList<(int Row, int Column)> Hunks,   // change blocks: anchor row + 1-based col of first change
        int Added, int Removed);                       // git-style line stats

    /// <summary>Per-pane change navigation for the N-pane view: the pane's editor +
    /// its change blocks (rows where this pane differs from the base). The base
    /// pane (index 0) carries no hunks — it's the reference.</summary>
    public sealed record PaneNav(
        int Index, bool IsBase, TextEditor Editor,
        IReadOnlyList<(int Row, int Column)> Hunks);

    /// <summary>Result of <see cref="BuildMulti"/>: the editor grid + one
    /// <see cref="PaneNav"/> per column so each target can host its own navigator.</summary>
    public sealed record MultiBuilt(Control Body, IReadOnlyList<PaneNav> Panes);

    /// <summary>Per-visual-line diff classification for the background renderer.
    /// Partial = a replace (light line wash + dark changed words); Whole = a pure
    /// add/delete where the entire line is the change (dark wash across it all).</summary>
    private enum LineKind { None, Partial, Whole, Filler }

    public static Built Build(EnvPane left, EnvPane right, bool isDark)
    {
        var rows = MergeRows(left.Lines, right.Lines);

        // Brushes (theme-aware, resolved once).
        var delLine = Resolve("App.DiffDelLineBrush", isDark ? 0x26 : 0xFF, isDark ? 0xF85149u : 0xFFEBE9u);
        var delWord = Resolve("App.DiffDelWordBrush", isDark ? 0x59 : 0xFF, isDark ? 0xF85149u : 0xFFC0BCu);
        var addLine = Resolve("App.DiffAddLineBrush", isDark ? 0x26 : 0xFF, isDark ? 0x3FB950u : 0xE6FFECu);
        var addWord = Resolve("App.DiffAddWordBrush", isDark ? 0x59 : 0xFF, isDark ? 0x3FB950u : 0xABF2BCu);
        var filler  = Resolve("App.DiffFillerBrush",  isDark ? 0x0D : 0x0A, isDark ? 0x808080u : 0x000000u);
        var gutterFg = Resolve("App.TextSecondaryBrush", 0x99, 0x808080u);

        // ── Left / right documents + per-row info ─────────────────────────
        var leftText  = string.Join("\n", rows.Select(r => r.L?.Text ?? ""));
        var rightText = string.Join("\n", rows.Select(r => r.R?.Text ?? ""));

        var leftKinds = new LineKind[rows.Count];
        var rightKinds = new LineKind[rows.Count];
        var leftWords = new IReadOnlyList<CharDiff.DiffSegment>?[rows.Count];
        var rightWords = new IReadOnlyList<CharDiff.DiffSegment>?[rows.Count];
        var leftNums = new int[rows.Count];
        var rightNums = new int[rows.Count];

        for (int r = 0; r < rows.Count; r++)
        {
            var (l, rt) = rows[r];
            leftKinds[r]  = Classify(l);
            rightKinds[r] = Classify(rt);
            leftWords[r]  = WordSegs(l);
            rightWords[r] = WordSegs(rt);
            leftNums[r]   = l?.Number ?? 0;
            rightNums[r]  = rt?.Number ?? 0;
        }

        var leftLineToRow = new List<int>(left.Lines.Count);
        for (int i = 0; i < left.Lines.Count; i++) leftLineToRow.Add(0);
        for (int r = 0; r < rows.Count; r++)
        {
            var l = rows[r].L;
            if (l is not null && l.Number >= 1 && l.Number - 1 < leftLineToRow.Count)
                leftLineToRow[l.Number - 1] = r;
        }

        // Colour direction follows the sync direction (source → target), NOT
        // git's old/new. The LEFT pane is the SOURCE (what will be written) so
        // its changes are ADDITIONS → green; the RIGHT pane is the TARGET (what
        // will be overwritten) so its changes are REMOVALS → red.
        var editorL = MakeEditor(leftText, isDark, leftKinds, addLine, filler, leftWords, addWord, leftNums, gutterFg);
        var editorR = MakeEditor(rightText, isDark, rightKinds, delLine, filler, rightWords, delWord, rightNums, gutterFg);

        // Same extent-tolerant sync the N-pane view uses, so the 2-pane diff no
        // longer pins the wider side to the narrower side's max width.
        SyncScrollMulti(new List<TextEditor> { editorL, editorR });

        // ── Hunks (contiguous changed rows) + git-style line stats ──────────
        var hunks = new List<(int Row, int Column)>();
        int added = 0, removed = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            var (l, rt) = rows[r];
            if (l is { State: LineState.Different }) added++;    // source change → addition (green)
            if (rt is { State: LineState.Different }) removed++; // target change → removal (red)
        }
        int rr = 0;
        while (rr < rows.Count)
        {
            if (!IsChangedRow(rows[rr])) { rr++; continue; }
            int start = rr;
            while (rr < rows.Count && IsChangedRow(rows[rr])) rr++;
            hunks.Add((start, FirstChangedColumn(rows[start].L)));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(editorL, 0);
        var splitter = new GridSplitter
        {
            Width = 6,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Transparent,
            ShowsPreview = false,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(editorR, 2);
        grid.Children.Add(editorL);
        grid.Children.Add(splitter);
        grid.Children.Add(editorR);

        return new Built(grid, editorL, editorR, leftLineToRow, hunks, added, removed);
    }

    /// <summary>
    /// N-pane view (1, or 3+ environments), aligned to a BASE (pane 0). Every
    /// other pane is given a real 2-way diff against the base — word-level
    /// highlighting, red for its changes/additions, and rows aligned to the
    /// base line structure (a unified merge, so matched lines stay level across
    /// all columns). The base column is green where it differs from any pane.
    /// So the git-quality diff applies no matter how many envs are compared.
    /// One AvaloniaEdit editor per pane (SQL highlighting, find), scroll-synced.
    /// </summary>
    public static MultiBuilt BuildMulti(IReadOnlyList<EnvPane> panes, bool isDark, bool ignoreWhitespace)
    {
        int N = panes.Count;

        var addLine  = Resolve("App.DiffAddLineBrush", isDark ? 0x26 : 0xFF, isDark ? 0x3FB950u : 0xE6FFECu);
        var addWord  = Resolve("App.DiffAddWordBrush", isDark ? 0x59 : 0xFF, isDark ? 0x3FB950u : 0xABF2BCu);
        var delLine  = Resolve("App.DiffDelLineBrush", isDark ? 0x26 : 0xFF, isDark ? 0xF85149u : 0xFFEBE9u);
        var delWord  = Resolve("App.DiffDelWordBrush", isDark ? 0x59 : 0xFF, isDark ? 0xF85149u : 0xFFC0BCu);
        var filler   = Resolve("App.DiffFillerBrush", isDark ? 0x0D : 0x0A, isDark ? 0x808080u : 0x000000u);
        var gutterFg = Resolve("App.TextSecondaryBrush", 0x99, 0x808080u);

        // Per-column render data, built row-by-row so every column ends with the
        // same number of lines → the rows line up across all editors.
        var docLines = new List<string>[N];
        var kinds    = new List<LineKind>[N];
        var words    = new List<IReadOnlyList<CharDiff.DiffSegment>?>[N];
        var nums     = new List<int>[N];
        for (int i = 0; i < N; i++) { docLines[i] = new(); kinds[i] = new(); words[i] = new(); nums[i] = new(); }

        void AddCell(int col, string text, LineKind kind, IReadOnlyList<CharDiff.DiffSegment>? seg, int num)
        { docLines[col].Add(text); kinds[col].Add(kind); words[col].Add(seg); nums[col].Add(num); }

        if (N == 1)
        {
            foreach (var l in panes[0].Lines) AddCell(0, l.Text, LineKind.None, null, l.Number);
        }
        else
        {
            // Align each non-base pane to the base (pane 0).
            var baseSide = new IReadOnlyList<AlignedPaneLine>[N];
            var paneSide = new IReadOnlyList<AlignedPaneLine>[N];
            for (int i = 1; i < N; i++)
            {
                var (a, b) = LineAligner.AlignPair(panes[0].Definition, panes[i].Definition, ignoreWhitespace);
                baseSide[i] = a; paneSide[i] = b;
            }
            var baseAligned = baseSide[1];
            int M = baseAligned.Count;

            // base line → matching pane line (or null = removed in that pane);
            // and inserts (pane lines absent from the base) grouped by the base
            // line they follow.
            var matchByBase = new AlignedPaneLine?[N][];
            var insertsAfter = new Dictionary<int, List<AlignedPaneLine>>[N];
            for (int i = 1; i < N; i++)
            {
                matchByBase[i] = new AlignedPaneLine?[M];
                for (int b = 0; b < M; b++)
                {
                    int pj = baseSide[i][b].PairIndex;
                    matchByBase[i][b] = pj >= 0 ? paneSide[i][pj] : null;
                }
                var ins = new Dictionary<int, List<AlignedPaneLine>>();
                int lastBase = -1;
                foreach (var pl in paneSide[i])
                {
                    if (pl.PairIndex >= 0) lastBase = pl.PairIndex;
                    else { if (!ins.TryGetValue(lastBase, out var list)) ins[lastBase] = list = new(); list.Add(pl); }
                }
                insertsAfter[i] = ins;
            }

            void EmitInsertRows(int afterB)
            {
                for (int i = 1; i < N; i++)
                {
                    if (!insertsAfter[i].TryGetValue(afterB, out var list)) continue;
                    foreach (var pl in list)
                        for (int col = 0; col < N; col++)
                            if (col == i) AddCell(col, pl.Text, LineKind.Whole, null, pl.Number); // added in pane i
                            else          AddCell(col, "", LineKind.Filler, null, 0);
                }
            }

            EmitInsertRows(-1);
            for (int b = 0; b < M; b++)
            {
                bool baseDiffers = false;
                for (int i = 1; i < N; i++)
                {
                    var pl = matchByBase[i][b];
                    if (pl is null || pl.State == LineState.Different) { baseDiffers = true; break; }
                }
                AddCell(0, baseAligned[b].Text, baseDiffers ? LineKind.Partial : LineKind.None, null, baseAligned[b].Number);
                for (int i = 1; i < N; i++)
                {
                    var pl = matchByBase[i][b];
                    if (pl is null)                          AddCell(i, "", LineKind.Filler, null, 0);
                    else if (pl.State == LineState.Different) AddCell(i, pl.Text, LineKind.Partial, pl.Segments, pl.Number);
                    else                                     AddCell(i, pl.Text, LineKind.None, null, pl.Number);
                }
                EmitInsertRows(b);
            }
        }

        var grid = new Grid();
        for (int i = 0; i < N; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 160 });
            if (i < N - 1) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var editors = new List<TextEditor>();
        for (int i = 0; i < N; i++)
        {
            var lineBrush = i == 0 ? addLine : delLine;   // base green, others red
            var wordBrush = i == 0 ? addWord : delWord;
            var ed = MakeEditor(string.Join("\n", docLines[i]), isDark,
                kinds[i].ToArray(), lineBrush, filler, words[i].ToArray(), wordBrush, nums[i].ToArray(), gutterFg);
            editors.Add(ed);
            Grid.SetColumn(ed, i * 2);
            grid.Children.Add(ed);

            if (i < N - 1)
            {
                var sp = new GridSplitter
                {
                    Width = 6, ResizeDirection = GridResizeDirection.Columns,
                    Background = Brushes.Transparent, ShowsPreview = false,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetColumn(sp, i * 2 + 1);
                grid.Children.Add(sp);
            }
        }

        SyncScrollMulti(editors);

        // Per-pane change navigation. For every NON-base pane, the rows where its
        // cell differs from the base: a matched-but-different line (Partial), a
        // line it inserted (Whole), or a base line it's missing (a Filler sitting
        // over a REAL base row — not another pane's insert). Contiguous change
        // rows collapse into one hunk, anchored at its first row + first changed
        // column (so nav reveals the exact spot horizontally). Base pane = no nav.
        var navs = new List<PaneNav>(N);
        for (int i = 0; i < N; i++)
        {
            if (i == 0) { navs.Add(new PaneNav(0, true, editors[0], Array.Empty<(int, int)>())); continue; }

            var col = i;
            bool IsChangeRow(int row)
                => kinds[col][row] == LineKind.Partial
                || kinds[col][row] == LineKind.Whole
                || (kinds[col][row] == LineKind.Filler && kinds[0][row] != LineKind.Filler);

            int FirstCol(int row)
            {
                if (kinds[col][row] != LineKind.Partial) return 1;
                var segs = words[col][row];
                if (segs is null) return 1;
                int c = 0;
                foreach (var seg in segs)
                {
                    if (seg.Kind != CharDiff.SegmentKind.Equal) return c + 1;
                    c += seg.Text.Length;
                }
                return 1;
            }

            var hunks = new List<(int Row, int Column)>();
            int r = 0, total = kinds[col].Count;
            while (r < total)
            {
                if (!IsChangeRow(r)) { r++; continue; }
                int start = r;
                while (r < total && IsChangeRow(r)) r++;
                hunks.Add((start, FirstCol(start)));
            }
            navs.Add(new PaneNav(i, false, editors[i], hunks));
        }

        return new MultiBuilt(grid, navs);
    }

    /// <summary>
    /// A compact per-target change navigator (▲ ▼ + "k / N") for the N-pane view,
    /// mirroring the 2-pane header's nav. Self-contained: it owns its hunk index
    /// and drives its pane's editor (scroll-sync moves the others), centring the
    /// change row vertically and revealing its column horizontally — the same jump
    /// the 2-pane navigator does. Intended for non-base panes with ≥1 hunk.
    /// </summary>
    public static Control BuildPaneNavigator(PaneNav nav)
    {
        var hunks = nav.Hunks;
        var editor = nav.Editor;
        int idx = -1;

        var counter = new TextBlock
        {
            FontSize = 12.5, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        void UpdateCounter() => counter.Text = idx >= 0 ? $"{idx + 1}/{hunks.Count}" : $"{hunks.Count}";
        UpdateCounter();

        int CenterRow()
        {
            var tv = editor.TextArea.TextView;
            double lh = tv.DefaultLineHeight > 0 ? tv.DefaultLineHeight : 16;
            double centerY = tv.ScrollOffset.Y + tv.Bounds.Height / 2.0;
            return (int)(centerY / lh);
        }
        void Jump()
        {
            if (idx < 0 || idx >= hunks.Count) return;
            var (row, hcol) = hunks[idx];
            var tv = editor.TextArea.TextView;
            try
            {
                var doc = editor.Document;
                int line = row + 1;
                if (doc is not null && line >= 1 && line <= doc.LineCount)
                {
                    var dl = doc.GetLineByNumber(line);
                    int c = Math.Min(Math.Max(1, hcol), dl.Length + 1);
                    editor.TextArea.Caret.Line = line;
                    editor.TextArea.Caret.Column = c;
                    double lh = tv.DefaultLineHeight > 0 ? tv.DefaultLineHeight : 16;
                    double lineTop = tv.GetVisualTopByDocumentLine(line);
                    double targetY = Math.Max(0, lineTop - tv.Bounds.Height / 2.0 + lh / 2.0);
                    double desiredX = (c - 1) * tv.WideSpaceWidth;
                    double targetX = Math.Max(0, desiredX - tv.Bounds.Width * 0.3);
                    if (tv is ILogicalScrollable ls) ls.Offset = new Vector(targetX, targetY);
                }
            }
            catch { /* never let a nav click throw */ }
            UpdateCounter();
        }
        void Next()
        {
            if (hunks.Count == 0) return;
            int refRow = idx >= 0 && idx < hunks.Count ? hunks[idx].Row : CenterRow();
            int ni = -1;
            for (int i = 0; i < hunks.Count; i++) if (hunks[i].Row > refRow) { ni = i; break; }
            idx = ni >= 0 ? ni : 0;
            Jump();
        }
        void Prev()
        {
            if (hunks.Count == 0) return;
            int refRow = idx >= 0 && idx < hunks.Count ? hunks[idx].Row : CenterRow();
            int pi = -1;
            for (int i = hunks.Count - 1; i >= 0; i--) if (hunks[i].Row < refRow) { pi = i; break; }
            idx = pi >= 0 ? pi : hunks.Count - 1;
            Jump();
        }

        Button Arrow(string glyph, string tip, Action onClick)
        {
            var b = new Button
            {
                Padding = new Thickness(5, 1), MinWidth = 0, MinHeight = 0,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = glyph, FontSize = 14, Opacity = 0.8 },
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Arrow("↑", "Previous change in this target (vs source)", Prev));
        panel.Children.Add(Arrow("↓", "Next change in this target (vs source)", Next));
        panel.Children.Add(counter);
        return panel;
    }

    /// <summary>
    /// Sync vertical + horizontal scroll across N editors, tolerant of panes
    /// with DIFFERENT horizontal extents (long lines on one side, short on the
    /// other). The pane the user is actively scrolling is the "driver"; its
    /// offset is pushed to the others, each of which clamps to its own content
    /// width (a narrower pane simply stops, showing everything it has).
    ///
    /// The critical part: a clamped set raises its own ScrollOffsetChanged, and
    /// if that echo were allowed to write back it would pin the WIDER pane to the
    /// NARROWER pane's max — the long lines could never be scrolled into view.
    /// So we ignore every scroll event that doesn't come from the current driver.
    /// A simple re-entrancy bool can't do this because AvaloniaEdit raises the
    /// echo asynchronously, after the bool has already been reset. The driver is
    /// released only once the offsets have settled (a Background-priority post,
    /// which runs after the echo notifications but during the idle between
    /// gestures), so a later scroll on a different pane can take over cleanly.
    /// </summary>
    private static void SyncScrollMulti(List<TextEditor> editors)
    {
        if (editors.Count < 2) return;
        var views = editors.Select(e => e.TextArea.TextView).ToList();

        TextView? driver = null;
        bool releaseQueued = false;

        foreach (var v in views)
        {
            var self = v;
            self.ScrollOffsetChanged += (_, _) =>
            {
                // Not the driver → this is an echo from a pane we're syncing
                // (possibly clamped). Ignore it so it can't bounce back.
                if (driver is not null && !ReferenceEquals(driver, self)) return;

                driver = self;
                var off = self.ScrollOffset;
                foreach (var to in views)
                {
                    if (ReferenceEquals(to, self)) continue;
                    if (to is ILogicalScrollable ls)
                    {
                        var cur = to.ScrollOffset;
                        if (Math.Abs(cur.Y - off.Y) > 0.5 || Math.Abs(cur.X - off.X) > 0.5)
                            ls.Offset = new Vector(off.X, off.Y);
                    }
                }

                // Release the driver after the echo events drain (Background runs
                // on idle, i.e. between gestures) so another pane can drive next.
                if (!releaseQueued)
                {
                    releaseQueued = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        driver = null;
                        releaseQueued = false;
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }
    }

    private static bool IsChangedRow((AlignedPaneLine? L, AlignedPaneLine? R) row)
        => row.L is null || row.R is null
        || row.L.State == LineState.Different || row.R.State == LineState.Different;

    /// <summary>1-based char column of the first changed segment on the left
    /// line (so change-nav can scroll horizontally to the exact spot); 1 for
    /// pure delete / insert / filler rows.</summary>
    private static int FirstChangedColumn(AlignedPaneLine? l)
    {
        if (l is not { State: LineState.Different, PairIndex: >= 0, Segments: { Count: > 0 } segs })
            return 1;
        int col = 0;
        foreach (var seg in segs)
        {
            if (seg.Kind == CharDiff.SegmentKind.Removed) return col + 1;
            col += seg.Text.Length;
        }
        return 1;
    }

    private static TextEditor MakeEditor(
        string text, bool isDark,
        LineKind[] kinds, IBrush lineBrush, IBrush fillerBrush,
        IReadOnlyList<CharDiff.DiffSegment>?[] words, IBrush wordBrush,
        int[] numbers, IBrush gutterFg)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(text),
            IsReadOnly = true,
            ShowLineNumbers = false,             // custom margin below renders original numbers
            FontFamily = new FontFamily(MonoFont),
            FontSize = FontSize,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;

        // Stop a click / focus from scrolling the view back to the top: the
        // editor raises RequestBringIntoView for the focused element, which the
        // parent scroll honours by jumping to it. Change-nav does its own
        // scrolling via the caret (a different path), so swallowing this is safe.
        editor.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);

        // Standard find (Ctrl+F): AvaloniaEdit's SearchPanel gives a real
        // find box with next / previous / highlight-all + match count, exactly
        // like a normal editor. Installed per editor (searches the focused
        // side); stored on Tag so the host can open it programmatically.
        try { editor.Tag = AvaloniaEdit.Search.SearchPanel.Install(editor); } catch { }

        // SQL syntax highlighting via TextMate (VS Code Dark+/Light+).
        try
        {
            var registry = new RegistryOptions(isDark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            var install = editor.InstallTextMate(registry);
            install.SetGrammar(registry.GetScopeByLanguageId("sql"));
        }
        catch { /* highlighting is a nicety; never block the diff on it */ }

        // Two-level diff: line wash behind (light for a replace, dark for a
        // whole add/delete), word wash on top for replaces.
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(kinds, lineBrush, wordBrush, fillerBrush));
        editor.TextArea.TextView.LineTransformers.Add(new DiffWordColorizer(words, wordBrush));

        // Original per-side line numbers.
        editor.TextArea.LeftMargins.Add(new MappedLineNumberMargin(numbers, gutterFg));

        return editor;
    }

    private static LineKind Classify(AlignedPaneLine? line)
        => line is null ? LineKind.Filler
         : line.State != LineState.Different ? LineKind.None
         : line.PairIndex >= 0 ? LineKind.Partial   // replace → light line + dark words
         : LineKind.Whole;                            // pure add/delete → dark whole line

    /// <summary>Word segments to strong-wash: only for a real replace (both sides
    /// paired). A pure add / delete is the whole line — the line wash conveys it.</summary>
    private static IReadOnlyList<CharDiff.DiffSegment>? WordSegs(AlignedPaneLine? line)
        => line is { State: LineState.Different, PairIndex: >= 0, Segments: { Count: > 0 } segs } ? segs : null;

    private static List<(AlignedPaneLine? L, AlignedPaneLine? R)> MergeRows(
        IReadOnlyList<AlignedPaneLine> a, IReadOnlyList<AlignedPaneLine> b)
    {
        var rows = new List<(AlignedPaneLine?, AlignedPaneLine?)>(Math.Max(a.Count, b.Count));
        int ai = 0, bi = 0;
        while (ai < a.Count || bi < b.Count)
        {
            if (ai < a.Count && bi < b.Count && a[ai].PairIndex == bi) { rows.Add((a[ai], b[bi])); ai++; bi++; }
            else if (ai < a.Count && a[ai].PairIndex < 0) { rows.Add((a[ai], null)); ai++; }
            else if (bi < b.Count && b[bi].PairIndex < 0) { rows.Add((null, b[bi])); bi++; }
            else if (ai < a.Count) { rows.Add((a[ai], null)); ai++; }
            else { rows.Add((null, b[bi])); bi++; }
        }
        return rows;
    }

    private static IBrush Resolve(string key, int alpha, uint rgb)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush br)
            return br;
        return new SolidColorBrush(Color.FromArgb((byte)alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    // ───────────────────────── renderers ─────────────────────────

    /// <summary>Paints a full-width wash behind changed / filler lines: light for
    /// a replace (words get a stronger wash on top), dark for a whole add/delete
    /// (the entire line is the change), faint for a filler.</summary>
    private sealed class DiffLineBackgroundRenderer : IBackgroundRenderer
    {
        private readonly LineKind[] _kinds;
        private readonly IBrush _lineBrush;    // light wash — partial (replace)
        private readonly IBrush _wholeBrush;   // strong wash — whole add/delete
        private readonly IBrush _fillerBrush;

        public DiffLineBackgroundRenderer(LineKind[] kinds, IBrush lineBrush, IBrush wholeBrush, IBrush fillerBrush)
        {
            _kinds = kinds; _lineBrush = lineBrush; _wholeBrush = wholeBrush; _fillerBrush = fillerBrush;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            double width = textView.Bounds.Width;
            foreach (var vl in textView.VisualLines)
            {
                int row = vl.FirstDocumentLine.LineNumber - 1;
                if (row < 0 || row >= _kinds.Length) continue;
                var brush = _kinds[row] switch
                {
                    LineKind.Partial => _lineBrush,
                    LineKind.Whole   => _wholeBrush,
                    LineKind.Filler  => _fillerBrush,
                    _                => null,
                };
                if (brush is null) continue;
                double top = vl.VisualTop - textView.VerticalOffset;
                drawingContext.FillRectangle(brush, new Rect(0, top, width, vl.Height));
            }
        }
    }

    /// <summary>Strong-washes the changed words within replace lines.</summary>
    private sealed class DiffWordColorizer : DocumentColorizingTransformer
    {
        private readonly IReadOnlyList<CharDiff.DiffSegment>?[] _words;
        private readonly IBrush _wordBrush;

        public DiffWordColorizer(IReadOnlyList<CharDiff.DiffSegment>?[] words, IBrush wordBrush)
        {
            _words = words; _wordBrush = wordBrush;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            int row = line.LineNumber - 1;
            if (row < 0 || row >= _words.Length) return;
            var segs = _words[row];
            if (segs is null) return;

            int pos = 0;
            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;
            foreach (var seg in segs)
            {
                int len = seg.Text.Length;
                if (seg.Kind != CharDiff.SegmentKind.Equal && len > 0)
                {
                    int s = lineStart + pos;
                    int e = Math.Min(s + len, lineEnd);
                    if (s < e)
                        ChangeLinePart(s, e, el => el.TextRunProperties.SetBackgroundBrush(_wordBrush));
                }
                pos += len;
            }
        }
    }

    /// <summary>Line-number margin that shows each row's ORIGINAL source line
    /// number (blank for filler rows), instead of the padded document line.</summary>
    private sealed class MappedLineNumberMargin : LineNumberMargin
    {
        private readonly int[] _numbers;       // per document line (0-based row): original number, 0 = filler
        private readonly IBrush _foreground;
        private readonly Typeface _typeface;
        private readonly int _maxDigits;

        public MappedLineNumberMargin(int[] numbers, IBrush foreground)
        {
            _numbers = numbers;
            _foreground = foreground;
            _typeface = new Typeface(new FontFamily(MonoFont));
            int max = 0;
            foreach (var n in numbers) if (n > max) max = n;
            _maxDigits = Math.Max(2, max.ToString(CultureInfo.InvariantCulture).Length);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var ft = new FormattedText(new string('9', _maxDigits), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, _typeface, FontSize, _foreground);
            return new Size(ft.Width + 12, 0);
        }

        public override void Render(DrawingContext drawingContext)
        {
            var textView = TextView;
            if (textView is not { VisualLinesValid: true }) return;
            double width = Bounds.Size.Width;
            foreach (var vl in textView.VisualLines)
            {
                int row = vl.FirstDocumentLine.LineNumber - 1;
                if (row < 0 || row >= _numbers.Length) continue;
                int num = _numbers[row];
                if (num <= 0) continue;   // filler row → no number
                var ft = new FormattedText(num.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, _typeface, FontSize, _foreground);
                double y = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
                drawingContext.DrawText(ft, new Point(width - ft.Width - 6, y));
            }
        }
    }
}
