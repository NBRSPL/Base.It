using System.Globalization;
using Avalonia.Data.Converters;

namespace Base.It.App.ViewModels;

/// <summary>Converts an int count to true/false. Used to hide empty rows.</summary>
public sealed class IntToBoolConverter : IValueConverter
{
    public static readonly IntToBoolConverter NotZero = new(v => v != 0);

    private readonly Func<int, bool> _f;
    public IntToBoolConverter(Func<int, bool> f) => _f = f;

    public object? Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is int i && _f(i);
    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
