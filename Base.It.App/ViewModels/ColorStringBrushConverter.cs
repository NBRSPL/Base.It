using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Base.It.App.ViewModels;

/// <summary>
/// Converts a hex string like "#3478F6" to a SolidColorBrush.
/// Empty or invalid values fall back to the theme accent brush so badges
/// always look reasonable even without a user-chosen colour.
/// </summary>
public sealed class ColorStringBrushConverter : IValueConverter
{
    public static readonly ColorStringBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s) &&
            Color.TryParse(s, out var color))
            return new SolidColorBrush(color);

        if (Application.Current is { } app &&
            app.TryGetResource("AccentFillColorDefaultBrush", app.ActualThemeVariant, out var res) &&
            res is IBrush brush)
            return brush;

        return Brushes.SlateBlue;
    }

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
