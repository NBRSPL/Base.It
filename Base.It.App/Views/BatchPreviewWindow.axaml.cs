using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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

    public BatchPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
        Opened += async (_, _) => { if (_vm is not null) await _vm.LoadAsync(); };
        DetachedFromVisualTree += (_, _) => Unbind();
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
    private void OnVmPropertyChanged(object? s, PropertyChangedEventArgs e) { /* no-op for now */ }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

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
        PopulateInlines(text, pane.Lines);

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

    private static Control BuildHeader(EnvPane pane)
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

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(meta,  1);
        header.Children.Add(badge);
        header.Children.Add(meta);

        return new Border
        {
            Padding = new Thickness(10, 6),
            Child = header
        };
    }

    private static void PopulateInlines(SelectableTextBlock target, IReadOnlyList<AlignedPaneLine> lines)
    {
        target.Inlines?.Clear();
        if (target.Inlines is null) target.Inlines = new InlineCollection();

        var diffBg = ResolveBrush("App.DiffBgBrush",
            new SolidColorBrush(Color.FromArgb(0xFF, 0x3F, 0x2A, 0x14)));
        var diffFg = ResolveBrush("App.DiffFgBrush",
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD0, 0x89)));

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var run = new Run(line.Text);
            if (line.State == LineState.Different)
            {
                run.Background = diffBg;
                run.Foreground = diffFg;
                run.FontWeight = FontWeight.SemiBold;
            }
            target.Inlines.Add(run);
            if (i < lines.Count - 1) target.Inlines.Add(new LineBreak());
        }
    }

    private static IBrush ResolveBrush(string key, IBrush fallback)
        => Application.Current!.TryGetResource(key, null, out var r) && r is IBrush b ? b : fallback;
}
