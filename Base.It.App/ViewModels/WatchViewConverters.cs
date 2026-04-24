using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Base.It.App.ViewModels;

/// <summary>Returns a right-arrow when collapsed, down-arrow when expanded. Ascii so no font dependency.</summary>
public sealed class ExpanderGlyphConverter : IValueConverter
{
    public static readonly ExpanderGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "\u25BE" : "\u25B8";  // ▾ / ▸

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a bool to a muted opacity — used by empty section headers so they recede visually.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter MutedWhenTrue = new() { MutedValue = 0.45, NormalValue = 1.0 };

    public double MutedValue { get; init; } = 0.45;
    public double NormalValue { get; init; } = 1.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? MutedValue : NormalValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a drift status string to a foreground brush: amber for Different,
/// orange for MissingInTarget, red for Error, grey for the rest. Keeps the
/// grid dense without having to maintain per-row triggers.
/// </summary>
public sealed class DriftStatusBrushConverter : IValueConverter
{
    public static readonly DriftStatusBrushConverter Instance = new();

    private static readonly IBrush Different       = new SolidColorBrush(Color.Parse("#E0A800"));
    private static readonly IBrush MissingInTarget = new SolidColorBrush(Color.Parse("#E06D00"));
    private static readonly IBrush MissingInSource = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush Err             = new SolidColorBrush(Color.Parse("#D53935"));
    private static readonly IBrush InSync          = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush Fallback        = new SolidColorBrush(Color.Parse("#AAAAAA"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Different"       => Different,
            "MissingInTarget" => MissingInTarget,
            "MissingInSource" => MissingInSource,
            "Error"           => Err,
            "InSync"          => InSync,
            _                 => Fallback
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
