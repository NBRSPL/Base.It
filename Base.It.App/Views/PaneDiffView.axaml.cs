using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Base.It.App.ViewModels;
using Base.It.Core.Diff;

namespace Base.It.App.Views;

/// <summary>
/// Reusable diff-pane host. Binds to a <see cref="BatchPreviewViewModel"/>
/// and renders its Panes side-by-side with the same LineAligner-driven
/// red-line highlighting <see cref="BatchPreviewWindow"/> uses.
///
/// Two entry points share this renderer: the standalone preview Window
/// (Batch / Scripts / Watch row eye-buttons) and the Sync screen which
/// embeds it inline so Compare and Sync are one workspace. Keeping the
/// rendering in one control means a fix to the diff visuals lands in
/// both places at once. Find-overlay logic stays on the Window — it's a
/// window-chrome affordance, not part of the pane rendering itself.
/// </summary>
public partial class PaneDiffView : UserControl
{
    private BatchPreviewViewModel? _vm;
    private string _findText = "";

    /// <summary>
    /// Per-pane scrollviewer + measured offset of each line — used by
    /// <see cref="ScrollToLine"/> so the change-navigation buttons can
    /// jump every pane to the same line without re-laying-out. Cleared
    /// + rebuilt on every <see cref="Rebuild"/>.
    /// </summary>
    private readonly List<(ScrollViewer Scroll, SelectableTextBlock Text)> _paneScrolls = new();

    /// <summary>
    /// Re-entrancy guard for <see cref="OnPaneScrollChanged"/>: setting
    /// one pane's offset fires ScrollChanged on it, which would loop
    /// straight back into the handler. Flag flips true for the duration
    /// of one fan-out.
    /// </summary>
    private bool _syncingScroll;

    /// <summary>
    /// The 2-pane git-style aligned view's LEFT editor + its left-line-index →
    /// visual-row map. Non-null only while the aligned (AvaloniaEdit) renderer
    /// is active; used by <see cref="ScrollToLine"/> so change-nav jumps the
    /// editor to the right row (scroll-sync moves the other side). Null in the
    /// flowing (N-pane) path, which uses <see cref="_paneScrolls"/> instead.
    /// </summary>
    private AvaloniaEdit.TextEditor? _alignedLeftEditor;
    private IReadOnlyList<int>? _alignedLeftLineToRow;

    /// <summary>
    /// Change-navigation state for the aligned view: the hunks (contiguous
    /// change blocks, each with its anchor row + first-changed column), the
    /// current position, the git-style line stats, and the header counter
    /// label to update. Non-null only while the aligned renderer is active.
    /// </summary>
    private IReadOnlyList<(int Row, int Column)>? _hunks;
    private int _hunkIndex = -1;
    private int _hunkAdded, _hunkRemoved;
    private TextBlock? _hunkCounter;

    /// <summary>
    /// Above this many lines the aligned renderer (one control per line ×
    /// both sides) would spawn too many controls; fall back to the lighter
    /// flowing renderer. Real SQL objects are far below this.
    /// </summary>
    private const int AlignedMaxLines = 2000;

    public PaneDiffView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    /// <summary>
    /// Scroll every pane to the given (0-based) line index so a click on
    /// the preview window's "next change" button puts both source and
    /// target on the same line. The estimate is based on monospace line
    /// height — close enough for the kind of "bring it into view" jump
    /// the user expects from a change-navigation button.
    /// </summary>
    public void ScrollToLine(int lineIndex)
    {
        if (lineIndex < 0) return;

        // Aligned (2-pane) view: scroll the left editor to the visual row for
        // this left-pane line index (rows include filler lines for inserts /
        // deletes, so index != row); scroll-sync moves the right side.
        if (_alignedLeftEditor is not null && _alignedLeftLineToRow is not null)
        {
            int row = lineIndex < _alignedLeftLineToRow.Count ? _alignedLeftLineToRow[lineIndex] : lineIndex;
            _alignedLeftEditor.ScrollTo(row + 1, 0);
            return;
        }

        // Flowing (N-pane) view: drive every pane's viewer. Approximate line
        // height for Cascadia Mono / Consolas at 12pt is ~16-17px; 16 keeps
        // the target a bit above centre. The 60px headroom pulls it off the
        // top edge.
        const double LineHeight = 16.0;
        var y = Math.Max(0, lineIndex * LineHeight - 60);
        foreach (var (scroll, _) in _paneScrolls)
        {
            scroll.Offset = new Avalonia.Vector(scroll.Offset.X, y);
        }
    }

    /// <summary>
    /// Apply a find-text filter to the rendered inlines. Called from the
    /// hosting Window's find overlay. Sync's inline use doesn't drive
    /// this — its toolbar exposes a separate object-name input that
    /// drives a fresh load via the VM, not an inline-text filter.
    /// </summary>
    public void SetFindText(string text)
    {
        var next = text ?? "";
        if (next == _findText) return;
        _findText = next;
        Rebuild();
    }

    /// <summary>
    /// Open the standard find bar (AvaloniaEdit's SearchPanel — find next /
    /// previous / highlight-all + match count) on the first editor, focusing it.
    /// Called from the hosting window's Ctrl+F.
    /// </summary>
    public void OpenFind()
    {
        var host = this.FindControl<Grid>("PanesHost");
        var editor = host?.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().FirstOrDefault();
        if (editor is null) return;
        editor.Focus();
        if (editor.Tag is AvaloniaEdit.Search.SearchPanel sp) sp.Open();
    }

    private void Bind()
    {
        Unbind();
        _vm = DataContext as BatchPreviewViewModel;
        if (_vm is null) return;
        _vm.Panes.CollectionChanged += OnPanesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    private void Unbind()
    {
        if (_vm is null) return;
        _vm.Panes.CollectionChanged -= OnPanesChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnPanesChanged(object? s, NotifyCollectionChangedEventArgs e) => Rebuild();
    private void OnVmPropertyChanged(object? s, PropertyChangedEventArgs e) { /* no-op */ }

    /// <summary>
    /// Per-pane Copy → puts that pane's definition on the clipboard.
    /// Wired from the icon button inside each pane's header
    /// (see <see cref="BuildHeader"/>). The button's Tag holds the pane's
    /// text so we don't have to look it up via index. Silent on
    /// clipboard hiccups.
    /// </summary>
    private async void OnCopyPaneClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string text) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null) return;
        try { await top.Clipboard.SetTextAsync(text); } catch { }
    }

    private void Rebuild()
    {
        var host = this.FindControl<Grid>("PanesHost");
        if (host is null || _vm is null) return;

        // Detach ScrollChanged from the old viewers before we orphan
        // them. Without this, the closures keep a live reference back
        // to PaneDiffView via the handler delegate — fine for GC
        // eventually, but the dead viewers would still fire their
        // last layout-driven ScrollChanged and the handler would try
        // to update the brand-new ones with stale offsets.
        foreach (var (sv, _) in _paneScrolls) sv.ScrollChanged -= OnPaneScrollChanged;
        _paneScrolls.Clear();
        host.Children.Clear();
        host.ColumnDefinitions.Clear();

        var panes = _vm.Panes.ToArray();
        if (panes.Length == 0) return;

        _alignedLeftEditor = null;
        _alignedLeftLineToRow = null;
        _hunks = null;
        _hunkIndex = -1;
        _hunkCounter = null;

        host.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        // Two panes → git-style row-aligned side-by-side (with hunk nav + the
        // red/green two-level highlight). One or 3+ panes → the N-pane editor
        // view (AvaloniaEdit editors, SQL highlighting, find, scroll-sync).
        Control view =
            panes.Length == 2 && Math.Max(panes[0].Lines.Count, panes[1].Lines.Count) <= AlignedMaxLines
                ? BuildAlignedTwoPane(panes[0], panes[1])
                : BuildMultiPane(panes);
        Grid.SetColumn(view, 0);
        host.Children.Add(view);
    }

    /// <summary>N-pane (1 or 3+) view: a header row of per-pane [label + copy]
    /// above the shared multi-editor body from <see cref="SqlDiffEditor.BuildMulti"/>.</summary>
    private Control BuildMultiPane(EnvPane[] panes)
    {
        var isDark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var built = SqlDiffEditor.BuildMulti(panes, isDark, _vm?.IgnoreWhitespace ?? false);
        var body = built.Body;

        var header = new Grid();
        for (int i = 0; i < panes.Length; i++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            if (i < panes.Length - 1) header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            Control colHeader = LabelAndCopy(panes[i]);
            // Each TARGET (non-base pane) gets its own change navigator, exactly
            // like the 2-pane view — step through that target's differences vs the
            // source. The base column (0) is the reference and has none.
            if (i < built.Panes.Count && !built.Panes[i].IsBase && built.Panes[i].Hunks.Count > 0)
            {
                var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                wrap.Children.Add(colHeader);
                wrap.Children.Add(SqlDiffEditor.BuildPaneNavigator(built.Panes[i]));
                colHeader = wrap;
            }
            Grid.SetColumn(colHeader, i * 2);
            header.Children.Add(colHeader);
        }

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = ResolveBrush("App.StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = grid,
        };
    }

    /// <summary>
    /// Scroll-sync handler: any pane's scroll → push its offset to every
    /// other pane so source and target stay aligned line-for-line as
    /// the user scrolls. The <see cref="_syncingScroll"/> guard breaks
    /// the obvious infinite loop. Offsets are clamped to each target
    /// pane's max so a shorter pane doesn't overshoot.
    /// </summary>
    private void OnPaneScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (sender is not ScrollViewer src) return;
        if (_paneScrolls.Count < 2) return;

        _syncingScroll = true;
        try
        {
            foreach (var (sv, _) in _paneScrolls)
            {
                if (ReferenceEquals(sv, src)) continue;
                var maxX = Math.Max(0, sv.Extent.Width  - sv.Viewport.Width);
                var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                var x = Math.Min(src.Offset.X, maxX);
                var y = Math.Min(src.Offset.Y, maxY);
                if (sv.Offset.X != x || sv.Offset.Y != y)
                    sv.Offset = new Vector(x, y);
            }
        }
        finally { _syncingScroll = false; }
    }

    /// <summary>
    /// Build the git-style 2-pane view: a combined header (both labels +
    /// copy + change-nav) above the shared row-aligned body from
    /// <see cref="AlignedDiffView"/>. Records the aligned viewer + row map
    /// so <see cref="ScrollToLine"/> can drive change navigation.
    /// </summary>
    private Control BuildAlignedTwoPane(EnvPane left, EnvPane right)
    {
        // Build the editor FIRST so the header's change-count / nav can read
        // the hunks and stats it computes.
        var isDark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var built = SqlDiffEditor.Build(left, right, isDark);
        _alignedLeftEditor    = built.Left;
        _alignedLeftLineToRow = built.LeftLineToRow;
        _hunks       = built.Hunks;
        _hunkAdded   = built.Added;
        _hunkRemoved = built.Removed;
        _hunkIndex   = -1;

        var header = BuildAlignedHeader(left, right);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(built.Body, 1);
        grid.Children.Add(header);
        grid.Children.Add(built.Body);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = ResolveBrush("App.StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = grid,
        };
    }

    /// <summary>Combined header for the aligned view: source label + copy docked
    /// left, target copy + label docked right (both always visible), and a
    /// centered git-style change summary (nav arrows, position, and the
    /// red −removed / green +added chips) filling the middle.</summary>
    private Control BuildAlignedHeader(EnvPane left, EnvPane right)
    {
        var dock = new DockPanel { LastChildFill = true };

        var leftGroup = LabelAndCopy(left);
        DockPanel.SetDock(leftGroup, Dock.Left);
        dock.Children.Add(leftGroup);

        var rightGroup = LabelAndCopy(right);
        DockPanel.SetDock(rightGroup, Dock.Right);
        dock.Children.Add(rightGroup);

        dock.Children.Add(BuildDiffSummary());   // fills the middle, centered

        return new Border { Padding = new Thickness(10, 6), Child = dock };
    }

    /// <summary>Env badge + its Copy button, kept together and docked to an edge
    /// so Copy is never clipped by the change summary.</summary>
    private Control LabelAndCopy(EnvPane pane)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (IBrush)ColorStringBrushConverter.Instance.Convert(
                pane.Color, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!,
            Child = new TextBlock
            {
                Text = pane.Label, Foreground = Brushes.White,
                FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            },
        };

        var copyBtn = new Button
        {
            Padding = new Thickness(7, 3), MinWidth = 0, MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = pane.Definition,
            Content = new TextBlock
            {
                Text = "",   // Segoe Fluent "Copy" glyph
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                FontSize = 15, Opacity = 0.75,
            },
        };
        ToolTip.SetTip(copyBtn, "Copy this side");
        copyBtn.Click += OnCopyPaneClick;

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(badge);
        sp.Children.Add(copyBtn);
        var diffBadge = BuildDiffBadge(pane.DiffBadge);
        if (diffBadge is not null) sp.Children.Add(diffBadge);
        return sp;
    }

    /// <summary>Per-column diff indicator for the N-way view (see
    /// <see cref="EnvPane.DiffBadge"/>): a muted "base" tag on the base column,
    /// a muted "in sync" tag on unchanged targets, and a red "≠ N" pill (with a
    /// +/−/~ breakdown tooltip) on any environment that differs from the base.
    /// Null in the 2-pane path (DiffBadge is unset there).</summary>
    private static Control? BuildDiffBadge(MultiDiffBadge? badge)
    {
        if (badge is null) return null;
        if (badge.IsBase) return MutedChip("base", "The base — every other column is compared against this");
        if (!badge.Differs) return MutedChip("in sync", "Identical to the base");

        var red = ResolveBrush("App.DiffDelGutterBrush", new SolidColorBrush(Color.FromRgb(0xED, 0x1C, 0x24)));
        var chip = new Border
        {
            Background = red, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"≠ {badge.Total}", Foreground = Brushes.White,
                FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            },
        };
        ToolTip.SetTip(chip, $"+{badge.Added} added · −{badge.Removed} removed · ~{badge.Changed} changed (vs base)");
        return chip;
    }

    private static Border MutedChip(string text, string tip)
    {
        var chip = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = ResolveBrush("App.StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text, Opacity = 0.7, FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            },
        };
        ToolTip.SetTip(chip, tip);
        return chip;
    }

    /// <summary>Centered change summary: nav arrows, position ("k of N"), and the
    /// git-style red −removed / green +added chips. Muted "in sync" when equal.</summary>
    private Control BuildDiffSummary()
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (_hunks is not { Count: > 0 })
        {
            sp.Children.Add(new TextBlock { Text = "in sync", Opacity = 0.55, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        sp.Children.Add(ViewNavArrow("↑", "Previous change (Shift+F3)", OnPrevHunkClick));
        sp.Children.Add(ViewNavArrow("↓", "Next change (F3)", OnNextHunkClick));

        _hunkCounter = new TextBlock
        {
            Text = HunkLabel(), FontSize = 12.5, Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0),
        };
        sp.Children.Add(_hunkCounter);

        // Red chip = removed (target overwritten); green chip = added (from source).
        sp.Children.Add(CountChip($"−{_hunkRemoved}", ResolveBrush("App.DiffDelGutterBrush", new SolidColorBrush(Color.FromRgb(0xED, 0x1C, 0x24)))));
        sp.Children.Add(CountChip($"+{_hunkAdded}",        ResolveBrush("App.DiffAddGutterBrush", new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)))));
        return sp;
    }

    private static Border CountChip(string text, IBrush bg)
        => new()
        {
            Background = bg, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeight.SemiBold },
        };

    /// <summary>Position through the change set ("k of N" once navigating, else
    /// "N changes"). The +/− totals live in the chips beside it.</summary>
    private string HunkLabel()
    {
        int n = _hunks?.Count ?? 0;
        if (n == 0) return "in sync";
        return _hunkIndex >= 0 ? $"{_hunkIndex + 1} of {n}" : $"{n} change{(n == 1 ? "" : "s")}";
    }

    private Button ViewNavArrow(string glyph, string tip, EventHandler<RoutedEventArgs> onClick)
    {
        var btn = new Button
        {
            Padding = new Thickness(5, 1), MinWidth = 0, MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = glyph, FontSize = 14, Opacity = 0.75 },
        };
        ToolTip.SetTip(btn, tip);
        btn.Click += onClick;
        return btn;
    }

    private void OnNextHunkClick(object? s, RoutedEventArgs e) => NextChange();
    private void OnPrevHunkClick(object? s, RoutedEventArgs e) => PrevChange();

    /// <summary>Advance to the next change block. Aligned view → hunk nav that
    /// jumps to the exact spot (line + column). Flowing view → the VM's
    /// per-line nav. Public so the window's F3 shortcut can drive it.</summary>
    public void NextChange()
    {
        if (_hunks is { Count: > 0 } hs && _alignedLeftEditor is not null)
        {
            // Step exactly one change forward from where we are: the reference
            // is the current hunk's row once we've navigated, or the view
            // centre on the very first click. Wrap to the first change.
            int refRow = ReferenceRow(hs);
            int idx = -1;
            for (int i = 0; i < hs.Count; i++) { if (hs[i].Row > refRow) { idx = i; break; } }
            _hunkIndex = idx >= 0 ? idx : 0;
            JumpToHunk();
        }
        else _vm?.NextChangeCommand.Execute(null);
    }

    public void PrevChange()
    {
        if (_hunks is { Count: > 0 } hs && _alignedLeftEditor is not null)
        {
            int refRow = ReferenceRow(hs);
            int idx = -1;
            for (int i = hs.Count - 1; i >= 0; i--) { if (hs[i].Row < refRow) { idx = i; break; } }
            _hunkIndex = idx >= 0 ? idx : hs.Count - 1;
            JumpToHunk();
        }
        else _vm?.PrevChangeCommand.Execute(null);
    }

    private int ReferenceRow(IReadOnlyList<(int Row, int Column)> hs)
        => _hunkIndex >= 0 && _hunkIndex < hs.Count ? hs[_hunkIndex].Row : CurrentCenterRow();

    /// <summary>0-based document row currently at the vertical centre of the
    /// aligned left editor's viewport — the reference point for "next/prev".</summary>
    private int CurrentCenterRow()
    {
        if (_alignedLeftEditor is null) return 0;
        var tv = _alignedLeftEditor.TextArea.TextView;
        double lh = tv.DefaultLineHeight > 0 ? tv.DefaultLineHeight : 16;
        double centerY = tv.ScrollOffset.Y + tv.Bounds.Height / 2.0;
        return (int)(centerY / lh);
    }

    private void JumpToHunk()
    {
        if (_hunks is null || _alignedLeftEditor is null) return;
        var (row, col) = _hunks[_hunkIndex];
        int line = row + 1;
        var ed = _alignedLeftEditor;
        var tv = ed.TextArea.TextView;
        try
        {
            var doc = ed.Document;
            if (doc is not null && line >= 1 && line <= doc.LineCount)
            {
                var dl = doc.GetLineByNumber(line);
                int c = Math.Min(Math.Max(1, col), dl.Length + 1);
                ed.TextArea.Caret.Line = line;
                ed.TextArea.Caret.Column = c;

                // Scroll so the change sits in the MIDDLE of the viewport
                // (vertically) and its column is revealed (horizontally),
                // computed directly from the line/column geometry — this is
                // exact even for a line that was below the fold, and doesn't
                // fight BringCaretToView's minimal-scroll behaviour. Scroll-sync
                // moves the other side to match.
                double lh = tv.DefaultLineHeight > 0 ? tv.DefaultLineHeight : 16;
                double lineTop = tv.GetVisualTopByDocumentLine(line);
                double targetY = Math.Max(0, lineTop - tv.Bounds.Height / 2.0 + lh / 2.0);
                double desiredX = (c - 1) * tv.WideSpaceWidth;
                double targetX = Math.Max(0, desiredX - tv.Bounds.Width * 0.3);
                if (tv is Avalonia.Controls.Primitives.ILogicalScrollable ls)
                    ls.Offset = new Avalonia.Vector(targetX, targetY);
            }
        }
        catch { /* never let a nav click throw */ }
        if (_hunkCounter is not null) _hunkCounter.Text = HunkLabel();
    }

    private Control BuildPane(EnvPane pane, bool isLast)
    {
        var header = BuildHeader(pane, isLast);

        var text = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(10, 6),
            Background = Brushes.Transparent
        };
        PopulateInlines(text, pane.Lines, _findText);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Content = text
        };
        // Cache (scrollviewer, text) for ScrollToLine: change-nav buttons
        // need to drive every pane to the same line without re-querying
        // the visual tree on every click. Subscribing here (not in
        // Rebuild) means each viewer is wired exactly once on creation —
        // matching the unsubscribe in Rebuild's tear-down.
        scroll.ScrollChanged += OnPaneScrollChanged;
        _paneScrolls.Add((scroll, text));

        // Line-number gutter pinned to the left of the code, sharing its
        // vertical scroll (see DiffPaneGutter).
        var body = DiffPaneGutter.Wrap(scroll, pane.Lines.Count);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = ResolveBrush("App.StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = grid
        };
    }

    private Control BuildHeader(EnvPane pane, bool isLast)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (IBrush)ColorStringBrushConverter.Instance.Convert(
                pane.Color, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!,
            Child = new TextBlock
            {
                Text = pane.Label,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            }
        };

        var changedCount = pane.Lines.Count(l => l.State == LineState.Different);
        var meta = new TextBlock
        {
            Text = changedCount == 0
                ? $"{pane.Lines.Count} lines, in sync"
                : $"{pane.Lines.Count} lines, {changedCount} differ",
            Opacity = 0.55, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var copyBtn = new Button
        {
            Padding         = new Thickness(6, 2),
            MinWidth        = 0,
            MinHeight       = 0,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag             = pane.Definition,
            Content         = new TextBlock
            {
                Text       = "",  // Copy glyph (Segoe Fluent Icons)
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                FontSize   = 13,
                Opacity    = 0.75,
            },
        };
        ToolTip.SetTip(copyBtn, "Copy");
        copyBtn.Click += OnCopyPaneClick;

        // Inline change-nav (only on the rightmost pane). Plain glyph
        // buttons — transparent background, no border — sat at the
        // right edge of the preview at the same vertical level as the
        // copy icon. The counter shows "N / M" so the user knows where
        // they are in the change set without a toolbar above the panes.
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto")
        };
        Grid.SetColumn(badge,   0);
        Grid.SetColumn(meta,    1);
        Grid.SetColumn(copyBtn, 5);
        header.Children.Add(badge);
        header.Children.Add(meta);
        header.Children.Add(copyBtn);

        if (isLast)
        {
            // Counter + arrows bind to VM properties; IsVisible binds to
            // HasChanges so they reveal themselves the moment the diff
            // produces at least one change. Rebuilding the pane on every
            // VM change would be wasteful, so we wire bindings once.
            var counter = new TextBlock
            {
                FontSize = 11, Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            counter.Bind(TextBlock.TextProperty,
                new Avalonia.Data.Binding(nameof(BatchPreviewViewModel.ChangeNavigationLabel)));
            counter.Bind(TextBlock.IsVisibleProperty,
                new Avalonia.Data.Binding(nameof(BatchPreviewViewModel.HasChanges)));
            Grid.SetColumn(counter, 2);
            header.Children.Add(counter);

            var prev = BuildNavArrow(col: 3, glyph: "▲", tip: "Previous change (Shift+F3)",
                command: nameof(BatchPreviewViewModel.PrevChangeCommand));
            var next = BuildNavArrow(col: 4, glyph: "▼", tip: "Next change (F3)",
                command: nameof(BatchPreviewViewModel.NextChangeCommand));
            prev.Bind(Visual.IsVisibleProperty,
                new Avalonia.Data.Binding(nameof(BatchPreviewViewModel.HasChanges)));
            next.Bind(Visual.IsVisibleProperty,
                new Avalonia.Data.Binding(nameof(BatchPreviewViewModel.HasChanges)));
            header.Children.Add(prev);
            header.Children.Add(next);
        }

        return new Border
        {
            Padding = new Thickness(10, 6),
            Child = header
        };
    }

    /// <summary>
    /// Build one change-nav arrow — plain triangle glyph, transparent
    /// background, no border, hover-only tooltip. Bound to a named
    /// command on the VM so the wrap-around behaviour (cycling
    /// forever through the change set) stays owned by
    /// <see cref="BatchPreviewViewModel"/>.
    /// </summary>
    private static Button BuildNavArrow(int col, string glyph, string tip, string command)
    {
        var btn = new Button
        {
            Padding         = new Thickness(4, 0),
            MinWidth        = 0,
            MinHeight       = 0,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text     = glyph,
                FontSize = 11,
                Opacity  = 0.75,
            },
        };
        Grid.SetColumn(btn, col);
        ToolTip.SetTip(btn, tip);
        btn.Bind(Button.CommandProperty, new Avalonia.Data.Binding(command));
        return btn;
    }

    /// <summary>
    /// Paint one pane's lines into the <see cref="SelectableTextBlock"/>.
    ///
    /// <para>Two render paths share this method:</para>
    /// <list type="number">
    ///   <item>2-pane mode: <see cref="AlignedPaneLine.Segments"/> is
    ///         populated by <see cref="LineAligner.AlignPair"/>. We paint
    ///         only the changed substrings highlighted — whitespace or
    ///         one-char edits no longer blanket the whole line. Removed
    ///         segments use the red theme pair, added use green; equal
    ///         segments render plain.</item>
    ///   <item>N-way mode (3+ panes, no per-pair segment list): fall back
    ///         to the legacy "whole-line highlight in amber" so multi-
    ///         target previews still light up differences without us
    ///         needing to decide which peer to char-diff against.</item>
    /// </list>
    ///
    /// <para>The Find overlay's highlight wins over both — it's the
    /// user's active search and should be visible regardless of
    /// underlying diff state.</para>
    /// </summary>
    private static void PopulateInlines(SelectableTextBlock target, IReadOnlyList<AlignedPaneLine> lines, string findText)
    {
        target.Inlines?.Clear();
        if (target.Inlines is null) target.Inlines = new InlineCollection();

        // Themed brushes — resolved once per build so light/dark toggle
        // re-runs PopulateInlines via Rebuild. Fallbacks match the
        // ThemeResources.axaml dark values so a missing key doesn't
        // produce an invisible diff.
        var diffBg = ResolveBrush("App.DiffBgBrush",     new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x5A, 0x00)));
        var diffFg = ResolveBrush("App.DiffFgBrush",     new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xF4, 0xC2)));
        var delBg  = ResolveBrush("App.DiffDelBgBrush",  new SolidColorBrush(Color.FromArgb(0xFF, 0x5C, 0x23, 0x33)));
        var delFg  = ResolveBrush("App.DiffDelFgBrush",  new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB3, 0xB3)));
        var addBg  = ResolveBrush("App.DiffAddBgBrush",  new SolidColorBrush(Color.FromArgb(0xFF, 0x21, 0x39, 0x2B)));
        var addFg  = ResolveBrush("App.DiffAddFgBrush",  new SolidColorBrush(Color.FromArgb(0xFF, 0xB6, 0xF0, 0xB6)));
        var findBg = ResolveBrush("App.FindMatchBgBrush",new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x8B, 0x57)));
        var findFg = ResolveBrush("App.FindMatchFgBrush",Brushes.White);

        var hasFind = !string.IsNullOrEmpty(findText);

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isDiff = line.State == LineState.Different;

            // 2-pane mode: per-segment kinds. Each kind contributes a
            // (bg, fg) pair so the theme can tune the contrast pairing
            // for its background — the previous "no-fg-override"
            // approach left dark-theme text invisible against the
            // light highlight bg.
            if (line.Segments is { Count: > 0 } segments)
            {
                foreach (var seg in segments)
                {
                    var (bg, fg) = seg.Kind switch
                    {
                        CharDiff.SegmentKind.Removed => (delBg,  delFg),
                        CharDiff.SegmentKind.Added   => (addBg,  addFg),
                        _                            => ((IBrush?)null, (IBrush?)null),
                    };
                    EmitWithFind(target.Inlines!, seg.Text, bg, fg, findText, hasFind, findBg, findFg);
                }
            }
            else
            {
                // N-way fallback: highlight the whole line in amber when
                // it's marked Different; otherwise plain.
                var bg = isDiff ? diffBg : null;
                var fg = isDiff ? diffFg : null;
                EmitWithFind(target.Inlines!, line.Text, bg, fg, findText, hasFind, findBg, findFg);
            }

            if (i < lines.Count - 1) target.Inlines.Add(new LineBreak());
        }
    }

    /// <summary>
    /// Emit a segment of text into the inlines collection, splitting on
    /// any find-text hits so the Find overlay's highlight overrides the
    /// diff colour on matched substrings. <paramref name="bg"/> /
    /// <paramref name="fg"/> are the diff colours for the surrounding
    /// segment (null = plain text).
    /// </summary>
    private static void EmitWithFind(
        InlineCollection target, string text,
        IBrush? bg, IBrush? fg,
        string findText, bool hasFind,
        IBrush findBg, IBrush findFg)
    {
        if (text.Length == 0) return;
        if (!hasFind)
        {
            AddRun(target, text, bg, fg, isStrong: bg is not null);
            return;
        }
        int from = 0;
        while (from < text.Length)
        {
            var hit = text.IndexOf(findText, from, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                AddRun(target, text.Substring(from), bg, fg, isStrong: bg is not null);
                return;
            }
            if (hit > from)
                AddRun(target, text.Substring(from, hit - from), bg, fg, isStrong: bg is not null);
            AddRun(target, text.Substring(hit, findText.Length), findBg, findFg, isStrong: true);
            from = hit + findText.Length;
        }
    }

    private static void AddRun(InlineCollection target, string segment, IBrush? bg, IBrush? fg, bool isStrong)
    {
        if (segment.Length == 0) return;
        var run = new Run(segment);
        if (bg is not null) run.Background = bg;
        if (fg is not null) run.Foreground = fg;
        if (isStrong)       run.FontWeight = FontWeight.SemiBold;
        target.Add(run);
    }

    /// <summary>
    /// Resolve a themed brush by key. CRITICAL: pass the current
    /// <c>ActualThemeVariant</c> — passing <c>null</c> only searches
    /// the global resource dictionary and misses every key defined
    /// inside <c>ResourceDictionary.ThemeDictionaries</c> (which is
    /// where ThemeResources.axaml puts the per-theme palette).
    /// Without the variant, the lookup silently falls through to the
    /// hardcoded fallback — that was why every diff-colour change
    /// I made was invisible: the renderer kept using the fallback
    /// red/green baked into the call sites below.
    /// </summary>
    private static IBrush ResolveBrush(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is null) return fallback;
        var theme = app.ActualThemeVariant;
        return app.TryGetResource(key, theme, out var r) && r is IBrush b ? b : fallback;
    }
}
