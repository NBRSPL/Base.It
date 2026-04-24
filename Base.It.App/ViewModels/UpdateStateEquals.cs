using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Base.It.App.Services;

namespace Base.It.App.ViewModels;

/// <summary>
/// IValueConverter that reports whether an <see cref="UpdateState"/> equals
/// the static instance's target. Used by SettingsView to gate which action
/// buttons / progress indicators are visible per updater phase.
/// </summary>
public sealed class UpdateStateEquals : IValueConverter
{
    public static readonly UpdateStateEquals Idle         = new(UpdateState.Idle);
    public static readonly UpdateStateEquals Checking     = new(UpdateState.Checking);
    public static readonly UpdateStateEquals Available    = new(UpdateState.Available);
    public static readonly UpdateStateEquals Downloading  = new(UpdateState.Downloading);
    public static readonly UpdateStateEquals ReadyToApply = new(UpdateState.ReadyToApply);
    public static readonly UpdateStateEquals Failed       = new(UpdateState.Failed);
    public static readonly UpdateStateEquals UpToDate     = new(UpdateState.UpToDate);

    private readonly UpdateState _target;
    private UpdateStateEquals(UpdateState target) { _target = target; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is UpdateState s && s == _target;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
