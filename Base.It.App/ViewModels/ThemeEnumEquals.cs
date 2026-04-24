using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Base.It.App.Services;

namespace Base.It.App.ViewModels;

/// <summary>
/// IValueConverter that returns true when the bound <see cref="AppSettingsStore.ThemePref"/>
/// equals the static instance's target. Used to drive the Dark/Light/System
/// radio buttons in Settings → Appearance.
///
/// RadioButton.IsChecked is TwoWay by default, so clicking a radio invokes
/// <see cref="ConvertBack"/>. We translate a true click into the target
/// enum (so the VM gets flipped to that preference) and a false click into
/// <see cref="BindingOperations.DoNothing"/> — another radio going false
/// shouldn't reset the whole value to some unrelated default.
/// </summary>
public sealed class ThemeEnumEquals : IValueConverter
{
    public static readonly ThemeEnumEquals Dark   = new(AppSettingsStore.ThemePref.Dark);
    public static readonly ThemeEnumEquals Light  = new(AppSettingsStore.ThemePref.Light);
    public static readonly ThemeEnumEquals System = new(AppSettingsStore.ThemePref.System);

    private readonly AppSettingsStore.ThemePref _target;
    private ThemeEnumEquals(AppSettingsStore.ThemePref target) { _target = target; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppSettingsStore.ThemePref p && p == _target;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? _target : BindingOperations.DoNothing;
}
