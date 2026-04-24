using System.Globalization;
using Avalonia.Data.Converters;
using Base.It.Core.Config;

namespace Base.It.App.ViewModels;

/// <summary>
/// A set of tiny converters used to toggle IsVisible on the Settings form
/// depending on which auth mode is selected.
/// </summary>
public sealed class AuthModeEquals : IValueConverter
{
    private readonly Func<AuthMode, bool> _match;

    public AuthModeEquals(Func<AuthMode, bool> match) => _match = match;

    public object? Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is AuthMode m && _match(m);

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();

    public static readonly AuthModeEquals Raw     = new(m => m == AuthMode.RawConnectionString);
    public static readonly AuthModeEquals Sql     = new(m => m == AuthMode.SqlAuth);
    public static readonly AuthModeEquals Windows = new(m => m == AuthMode.WindowsIntegrated);
    public static readonly AuthModeEquals NotRaw  = new(m => m != AuthMode.RawConnectionString);
}
