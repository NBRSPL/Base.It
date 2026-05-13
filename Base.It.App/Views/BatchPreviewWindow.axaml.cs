using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Base.It.App.ViewModels;
using Base.It.Core.Diff;

namespace Base.It.App.Views;

/// <summary>
/// Batch preview window. Renders the same multi-pane diff layout as
/// Compare, populated from <see cref="BatchPreviewViewModel"/>'s panes
/// (source + each ticked target). Diff highlighting reads
/// <c>App.DiffBgBrush</c> / <c>App.DiffFgBrush</c> from the active
/// theme so changed lines stay legible in both dark and light mode.
/// </summary>
public partial class BatchPreviewWindow : Window
{
    private BatchPreviewViewModel? _vm;
    private string _findText = "";

    public BatchPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
        Opened += (_, _) => Services.WindowSizing.ClampToWorkingArea(this);
        Opened += async (_, _) => { if (_vm is not null) await _vm.LoadAsync(); };
        DetachedFromVisualTree += (_, _) => Unbind();

        // Window-wide Ctrl+F so the find overlay is reachable from
        // anywhere in this window — mirrors MainWindow's behaviour so
        // the keystroke means the same thing in every Window that
        // shows text content. Esc closes via OnFindBoxKeyDown.
        AddHandler(KeyDownEvent, OnGlobalKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Bubble | Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Open the find overlay and seed it with the previous query so re-opening picks up where the user left off.</summary>
    private void OpenFindOverlay()
    {
        var ov  = this.FindControl<Border>("FindOverlay");
        var box = this.FindControl<TextBox>("FindBox");
        if (ov is null || box is null) return;
        ov.IsVisible = true;
        box.Text     = _findText;
        box.Focus();
        if (box.Text is { Length: > 0 } t) box.CaretIndex = t.Length;
    }

    private void HideFindOverlay()
    {
        var ov = this.FindControl<Border>("FindOverlay");
        if (ov is not null) ov.IsVisible = false;
        if (_findText.Length > 0)
        {
            _findText = "";
            Rebuild(); // clear match highlights
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenFindOverlay();
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFindOverlay();
            e.Handled = true;
        }
    }

    private void OnFindBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var next = tb.Text ?? "";
        if (next == _findText) return;
        _findText = next;
        Rebuild(); // re-render with new match highlights
    }

    private void OnFindClose(object? sender, RoutedEventArgs e) => HideFindOverlay();

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
    private void OnVmPropertyChanged(object? s, PropertyChangedEventArgs e) { /* no-op for now */ }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Per-pane Copy → puts that pane's definition on the clipboard.
    /// Wired from the icon button inside each pane's header (see
    /// <see cref="BuildHeader"/>). The button's Tag holds the pane's
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
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = text
        };

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

        // Per-pane Copy: a small icon button using the Segoe Fluent Icons
        // Copy glyph (), tooltip-only label. Tag carries the pane's
        // definition text so the handler doesn't need to walk the VM.
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
                Text       = "",
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

    private static void PopulateInlines(SelectableTextBlock target, IReadOnlyList<AlignedPaneLine> lines, string findText)
    {
        target.Inlines?.Clear();
        if (target.Inlines is null) target.Inlines = new InlineCollection();

        var diffBg = ResolveBrush("App.DiffBgBrush",
            new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x5A, 0x00)));
        var diffFg = ResolveBrush("App.DiffFgBrush",
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xF4, 0xC2)));

        // Find-match colour: distinct from the diff highlight so a line that
        // is BOTH "different" AND contains a find match shows both treatments
        // — the diff colour on the full line, with the match segment painted
        // on top in green. Hardcoded; doesn't need to be themed because the
        // overlay is a transient interaction.
        var findBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0x8B, 0x22));
        var findFg = Brushes.White;

        var hasFind = !string.IsNullOrEmpty(findText);

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isDiff = line.State == LineState.Different;

            // Split the line into runs at every case-insensitive match of
            // findText. Segments outside matches get diff styling (if the
            // whole line is "Different"); match segments get the find
            // colour stacked on top. No regex — plain substring search.
            if (hasFind)
            {
                var text = line.Text;
                int from = 0;
                while (from < text.Length)
                {
                    var hit = text.IndexOf(findText, from, StringComparison.OrdinalIgnoreCase);
                    if (hit < 0)
                    {
                        AddSegment(target.Inlines!, text.Substring(from), isDiff, diffBg, diffFg, false, findBg, findFg);
                        break;
                    }
                    if (hit > from)
                        AddSegment(target.Inlines!, text.Substring(from, hit - from), isDiff, diffBg, diffFg, false, findBg, findFg);
                    AddSegment(target.Inlines!, text.Substring(hit, findText.Length), isDiff, diffBg, diffFg, true, findBg, findFg);
                    from = hit + findText.Length;
                }
            }
            else
            {
                AddSegment(target.Inlines!, line.Text, isDiff, diffBg, diffFg, false, findBg, findFg);
            }

            if (i < lines.Count - 1) target.Inlines.Add(new LineBreak());
        }
    }

    private static void AddSegment(InlineCollection target, string segment,
        bool isDiff, IBrush diffBg, IBrush diffFg,
        bool isFindMatch, IBrush findBg, IBrush findFg)
    {
        if (segment.Length == 0) return;
        var run = new Run(segment);
        // Find-match wins over diff colour so the user always sees the
        // search hit, even on differing lines. SemiBold is preserved
        // either way.
        if (isFindMatch)
        {
            run.Background = findBg;
            run.Foreground = findFg;
            run.FontWeight = FontWeight.SemiBold;
        }
        else if (isDiff)
        {
            run.Background = diffBg;
            run.Foreground = diffFg;
            run.FontWeight = FontWeight.SemiBold;
        }
        target.Add(run);
    }

    private static IBrush ResolveBrush(string key, IBrush fallback)
        => Application.Current!.TryGetResource(key, null, out var r) && r is IBrush b ? b : fallback;
}
