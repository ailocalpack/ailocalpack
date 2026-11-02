using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace AiLocalPack.Desktop.Views;

/// <summary>Looks a resource up by the key a view model gives (a section's icon geometry).</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static ResourceKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key, out var resource) == true ? resource : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Upper-cases text in the UI language (group titles; XAML has no text-transform).</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public static UpperCaseConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(CultureInfo.CurrentUICulture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
