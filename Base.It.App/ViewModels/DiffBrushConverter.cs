using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Base.It.Core.Diff;

namespace Base.It.App.ViewModels;

/// <summary>
/// Maps DiffKind -> IBrush for inline background highlighting in the Compare view.
/// </summary>
public sealed class DiffBrushConverter : IMultiValueConverter, IValueConverter
{
    public static readonly DiffBrushConverter Instance = new();

    private static readonly IBrush Same       = Brushes.Transparent;
    private static readonly IBrush Changed    = new SolidColorBrush(Color.FromArgb(0x6E, 0xFF, 0x5E, 0x5E));
    private static readonly IBrush MissingInA = new SolidColorBrush(Color.FromArgb(0x6E, 0x5E, 0xCE, 0xFF));
    private static readonly IBrush MissingInB = new SolidColorBrush(Color.FromArgb(0x6E, 0xF0, 0xC7, 0x5E));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(values.FirstOrDefault(), targetType, parameter, culture);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DiffKind.Changed    => Changed,
            DiffKind.MissingInA => MissingInA,
            DiffKind.MissingInB => MissingInB,
            _                   => Same
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
