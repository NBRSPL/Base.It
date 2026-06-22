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
        // Approximate line height for Cascadia Mono / Consolas at 12pt
        // is ~16-17px; using 16 keeps the target line a bit above
        // centre so the user can see what follows. The 60px headroom
        // pulls the line off the top edge.
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

        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        _paneScrolls.Clear();

        var panes = _vm.Panes.ToArray();
        if (panes.Length == 0) return;

        for (int i = 0; i < panes.Length; i++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 200 });
            if (i < panes.Length - 1)
                host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (int i = 0; i < panes.Length; i++)
        {
            var pane = BuildPane(panes[i]);
            Grid.SetColumn(pane, i * 2);
            host.Children.Add(pane);

            if (i < panes.Length - 1)
            {
                var splitter = new GridSplitter
                {
                    Width = 6,
                    ResizeDirection = GridResizeDirection.Columns,
                    Background = Brushes.Transparent,
                    ShowsPreview = false,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                Grid.SetColumn(splitter, i * 2 + 1);
                host.Children.Add(splitter);
            }
        }
    }

    private Control BuildPane(EnvPane pane)
    {
        var header = BuildHeader(pane);

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
        // the visual tree on every click.
        _paneScrolls.Add((scroll, text));

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroll);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = ResolveBrush("App.StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = grid
        };
    }

    private Control BuildHeader(EnvPane pane)
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

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        Grid.SetColumn(badge,   0);
        Grid.SetColumn(meta,    1);
        Grid.SetColumn(copyBtn, 2);
        header.Children.Add(badge);
        header.Children.Add(meta);
        header.Children.Add(copyBtn);

        return new Border
        {
            Padding = new Thickness(10, 6),
            Child = header
        };
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

            // 2-pane mode: we have per-segment kinds. Render each
            // segment with the right brush — and overlay Find matches
            // on top of whatever the segment kind was.
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

    private static IBrush ResolveBrush(string key, IBrush fallback)
        => Application.Current!.TryGetResource(key, null, out var r) && r is IBrush b ? b : fallback;
}
