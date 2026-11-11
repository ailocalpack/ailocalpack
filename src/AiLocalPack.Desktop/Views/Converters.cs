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

/// <summary>
/// The keyboard focus ring's corners: the focused control's own <c>CornerRadius</c> (a templated control, a
/// border, a content presenter) grown by the ring's offset, so the ring follows a rounded button instead of
/// Fluent's square one. The value is the control from <c>AdornerLayer.AdornedElement</c> of the ring.
/// </summary>
public sealed class FocusRingCornerRadiusConverter : IValueConverter
{
    /// <summary>How far the ring's outer edge sits outside the control (the ring's negative margin).</summary>
    public const double Offset = 3;

    public static FocusRingCornerRadiusConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value switch
        {
            Avalonia.Controls.Primitives.TemplatedControl control => control.CornerRadius,
            Border border => border.CornerRadius,
            Avalonia.Controls.Presenters.ContentPresenter presenter => presenter.CornerRadius,
            _ => default,
        };
        return new CornerRadius(
            Grow(radius.TopLeft), Grow(radius.TopRight), Grow(radius.BottomRight), Grow(radius.BottomLeft));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double Grow(double corner) => corner + Offset;
}
