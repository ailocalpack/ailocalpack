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

/// <summary>
/// A PNG or JPEG file on this machine as a picture (the test run's screenshots), decoded at most 640 pixels wide;
/// <c>null</c> for a missing or unreadable file. Decoded pictures are kept by path and write time.
/// </summary>
public sealed class FileBitmapConverter : IValueConverter
{
    private const int DecodeWidth = 640;
    private static readonly Dictionary<string, (DateTime Written, Avalonia.Media.Imaging.Bitmap Bitmap)> Cache = new(StringComparer.Ordinal);

    public static FileBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
        {
            return null;
        }

        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var cached) && cached.Written == written)
            {
                return cached.Bitmap;
            }

            using var stream = File.OpenRead(path);
            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, DecodeWidth);
            Cache[path] = (written, bitmap);
            return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

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

/// <summary>The tallest a dialog may be in a window of the given height: the window less a margin above and below.</summary>
public sealed class DialogHeightConverter : IValueConverter
{
    public static DialogHeightConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double height && height > 0 ? Math.Max(320, height - 96) : double.PositiveInfinity;

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
