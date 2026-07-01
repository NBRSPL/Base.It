using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Base.It.App.Views;

/// <summary>
/// Builds a line-number gutter for a diff pane and pairs it with the pane's
/// code <see cref="ScrollViewer"/>. Shared by the Compare tab
/// (<see cref="CompareTabView"/>) and the Batch/Sync preview
/// (<see cref="PaneDiffView"/>) so both number lines identically.
///
/// The aligner emits contiguous lines per pane (no padding rows), so the
/// numbers are simply 1..N. The gutter lives in its own vertical-only
/// scroll viewer pinned to the left; it mirrors the code viewer's vertical
/// offset (via ScrollChanged) but never scrolls horizontally, so the numbers
/// stay put while the SQL scrolls sideways.
/// </summary>
internal static class DiffPaneGutter
{
    private const string MonoFont = "Cascadia Mono,Consolas,monospace";
    private const double FontSize = 12;

    /// <summary>
    /// Wrap <paramref name="codeScroll"/> in a [gutter | code] grid and return
    /// it. <paramref name="lineCount"/> is the number of rendered lines.
    /// </summary>
    public static Control Wrap(ScrollViewer codeScroll, int lineCount)
    {
        var gutter = new TextBlock
        {
            FontFamily     = new FontFamily(MonoFont),
            FontSize       = FontSize,
            TextWrapping   = TextWrapping.NoWrap,
            TextAlignment  = TextAlignment.Right,
            Padding        = new Thickness(8, 6, 8, 6),
            Opacity        = 0.45,
            IsHitTestVisible = false,            // never steal selection/clicks
            Text           = BuildNumbers(lineCount),
        };

        var gutterScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Hidden,
            Content = gutter,
        };

        var gutterBorder = new Border
        {
            BorderBrush     = ResolveBrush("App.StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child           = gutterScroll,
        };

        // Mirror the code pane's vertical scroll onto the gutter. The gutter
        // has no horizontal content, so X stays 0 and the numbers stay pinned.
        codeScroll.ScrollChanged += (_, _) =>
        {
            if (gutterScroll.Offset.Y != codeScroll.Offset.Y)
                gutterScroll.Offset = new Vector(0, codeScroll.Offset.Y);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(gutterBorder, 0);
        Grid.SetColumn(codeScroll,   1);
        grid.Children.Add(gutterBorder);
        grid.Children.Add(codeScroll);
        return grid;
    }

    private static string BuildNumbers(int count)
    {
        if (count <= 0) return string.Empty;
        var sb = new StringBuilder(count * 4);
        for (var i = 1; i <= count; i++)
        {
            if (i > 1) sb.Append('\n');
            sb.Append(i);
        }
        return sb.ToString();
    }

    private static IBrush ResolveBrush(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is null) return fallback;
        return app.TryGetResource(key, app.ActualThemeVariant, out var r) && r is IBrush b ? b : fallback;
    }
}
