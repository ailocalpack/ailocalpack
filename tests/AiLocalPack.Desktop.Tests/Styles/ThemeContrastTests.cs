using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace AiLocalPack.Desktop.Tests.Styles;

/// <summary>
/// The palette's text and fill pairs keep WCAG contrast in both themes: 4.5:1 for text, 3:1 for icons and the focus
/// ring (docs/desktop-app.md, <i>Controls and colours</i>). Translucent tokens are laid over the colour under them.
/// </summary>
public sealed class ThemeContrastTests
{
    private const double Text = 4.5;
    private const double Graphic = 3;

    public static TheoryData<string, string, string, double> Pairs()
    {
        var data = new TheoryData<string, string, string, double>();
        string[] themes = ["Dark", "Light"];
        foreach (var theme in themes)
        {
            foreach (var surface in new[] { "AppBackgroundBrush", "AppSurfaceBrush", "AppSurface2Brush" })
            {
                foreach (var text in new[]
                         {
                             "AppTextBrush", "AppMutedBrush", "AppFaintBrush", "AppAccentBrush", "AppOkBrush", "AppWarnBrush",
                             "AppBadBrush", "AppGenBrush",
                         })
                {
                    data.Add(theme, text, surface, Text);
                }
            }

            // A tone's text on its own tint (tags, notices, banners), over a card or the window
            foreach (var tone in new[] { "Accent", "Ok", "Warn", "Bad", "Gen" })
            {
                data.Add(theme, $"App{tone}Brush", $"App{tone}SoftBrush+AppSurfaceBrush", Text);
                data.Add(theme, $"App{tone}Brush", $"App{tone}SoftBrush+AppBackgroundBrush", Text);
            }

            // Text on filled controls, on every state
            data.Add(theme, "#ffffff", "AppAccentFillBrush", Text);
            data.Add(theme, "#ffffff", "AppAccentFillHoverBrush", Text);
            data.Add(theme, "#ffffff", "AppAccentFillPressedBrush", Text);
            data.Add(theme, "#ffffff", "AppBadFillBrush", Text);
            data.Add(theme, "#ffffff", "AppBadFillHoverBrush", Text);
            data.Add(theme, "AppOnWarnBrush", "AppWarnBrush", Text);
            data.Add(theme, "AppOnGenBrush", "AppGenBrush", Text);
            data.Add(theme, "AppOnOkBrush", "AppOkBrush", Graphic);
            data.Add(theme, "AppFocusBrush", "AppBackgroundBrush", Graphic);
            data.Add(theme, "AppFocusBrush", "AppSurfaceBrush", Graphic);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Foreground_reads_on_its_background(string theme, string foreground, string background, double minimum)
    {
        await HeadlessUi.RunAsync(() =>
        {
            // Arrange
            var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            var under = Solid(background, variant, Colors.White);
            var over = Solid(foreground, variant, under);

            // Act
            var ratio = Contrast(over, under);

            // Assert
            ratio.Should().BeGreaterThanOrEqualTo(minimum, $"{foreground} on {background} in the {theme} theme");
        });
    }

    /// <summary>A colour, a token, or tokens laid over each other ("top+bottom"), flattened onto <paramref name="under"/>.</summary>
    private static Color Solid(string spec, ThemeVariant variant, Color under)
    {
        var layers = spec.Split('+');
        var color = under;
        foreach (var layer in layers.Reverse())
        {
            color = Blend(Lookup(layer, variant), color);
        }

        return color;
    }

    private static Color Lookup(string key, ThemeVariant variant)
    {
        if (key.StartsWith('#'))
        {
            return Color.Parse(key);
        }

        Application.Current!.TryGetResource(key, variant, out var value).Should().BeTrue(key);
        return ((ISolidColorBrush)value!).Color;
    }

    private static Color Blend(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (light, dark) = (Luminance(a), Luminance(b)) is var (x, y) && x > y ? (x, y) : (y, x);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
