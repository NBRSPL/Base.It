using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Base.It.App.Services;

/// <summary>
/// IValueConverters for <see cref="ToastKind"/> — used by ToastHost.axaml to
/// toggle kind-specific classes and fetch the indicator brush from the
/// theme dictionary. Returning theme-keyed brushes via
/// <see cref="Application.Current.Resources"/> keeps the toast adaptive
/// when the user flips Dark/Light.
/// </summary>
public static class ToastKindConverters
{
    public static readonly IValueConverter IsSuccess = new KindEqualsConverter(ToastKind.Success);
    public static readonly IValueConverter IsError   = new KindEqualsConverter(ToastKind.Error);
    public static readonly IValueConverter IsWarning = new KindEqualsConverter(ToastKind.Warning);
    public static readonly IValueConverter IsInfo    = new KindEqualsConverter(ToastKind.Info);

    public static readonly IValueConverter ToBrush = new KindToBrushConverter();

    private sealed class KindEqualsConverter : IValueConverter
    {
        private readonly ToastKind _target;
        public KindEqualsConverter(ToastKind target) { _target = target; }
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is ToastKind k && k == _target;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class KindToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string key = value is ToastKind k ? k switch
            {
                ToastKind.Success => "App.SuccessBrush",
                ToastKind.Error   => "App.ErrorBrush",
                ToastKind.Warning => "App.WarningBrush",
                _                 => "App.InfoBrush",
            } : "App.InfoBrush";

            var app = Application.Current;
            if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res)
                && res is IBrush brush)
                return brush;
            return Brushes.SteelBlue;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
