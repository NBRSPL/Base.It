using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Base.It.App.ViewModels;

/// <summary>
/// Bridges a <see cref="DateTimeOffset"/>? VM property to a
/// <see cref="DateTime"/>? control property (CalendarDatePicker.SelectedDate
/// only takes DateTime). Treats the VM value as already in local time —
/// snipping the offset is fine because the picker shows wall-clock dates,
/// not absolute instants. ConvertBack maps the picker's chosen date back
/// to a DateTimeOffset at the same wall-clock local time.
/// </summary>
public sealed class DateTimeOffsetToDateTimeConverter : IValueConverter
{
    public static readonly DateTimeOffsetToDateTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto) return dto.LocalDateTime;
        if (value is DateTime dt)        return dt;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
        if (value is DateTimeOffset dto) return dto;
        return null;
    }
}

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
        // Accepts both the raw enum-name form ("MissingInTarget") and the
        // humanised label ("Missing in target") so we can pass either
        // shape through this single converter from XAML.
        => (value as string)?.ToLowerInvariant() switch
        {
            "different"             => Different,
            "missingintarget"       => MissingInTarget,
            "missing in target"     => MissingInTarget,
            "missinginsource"       => MissingInSource,
            "missing in source"     => MissingInSource,
            "error"                 => Err,
            "insync"                => InSync,
            "in sync"               => InSync,
            _                       => Fallback
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
