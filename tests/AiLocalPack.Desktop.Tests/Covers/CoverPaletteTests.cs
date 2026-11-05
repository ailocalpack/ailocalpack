using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Covers;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Tests.Covers;

/// <summary>Dominant colors of sample images (BGRA pixel buffers drawn in the test).</summary>
public sealed class CoverPaletteTests
{
    private const int Width = 60;
    private const int Height = 90;

    /// <summary>An image whose pixel (x, y) is <paramref name="paint"/>(x, y), opaque unless the color says otherwise.</summary>
    private static byte[] Image(Func<int, int, Color> paint)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var c = paint(x, y);
                var i = ((y * Width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, c.A);
            }
        }

        return pixels;
    }

    private static CoverColors? Extract(byte[] pixels) => CoverPalette.Extract(pixels, Width, Height, Width * 4);

    private static readonly Color Navy = Color.FromRgb(20, 30, 80);
    private static readonly Color Orange = Color.FromRgb(240, 150, 40);
    private static readonly Color Grey = Color.FromRgb(120, 120, 120);

    [Fact]
    public void Solid_image_gives_its_color_everywhere()
    {
        // Arrange
        var pixels = Image((_, _) => Navy);

        // Act
        var colors = Extract(pixels);

        // Assert
        colors.Should().Be(new CoverColors(Navy, Navy, Navy));
    }

    [Fact]
    public void Biggest_area_is_the_dominant_color()
    {
        // Arrange: top 70% navy, bottom 30% orange
        var pixels = Image((_, y) => y < Height * 0.7 ? Navy : Orange);

        // Act
        var colors = Extract(pixels);

        // Assert
        colors!.Value.Dominant.Should().Be(Navy);
    }

    [Fact]
    public void Vivid_color_is_the_glow_over_a_grey_dominant()
    {
        // Arrange: mostly grey with an orange stripe
        var pixels = Image((_, y) => y < Height * 0.85 ? Grey : Orange);

        // Act
        var colors = Extract(pixels);

        // Assert
        (colors!.Value.Dominant, colors.Value.Glow).Should().Be((Grey, Orange));
    }

    [Fact]
    public void Second_glow_is_a_third_color_when_there_is_one()
    {
        // Arrange: grey background, an orange and a navy band
        var pixels = Image((_, y) => y < 50 ? Grey : y < 70 ? Orange : Navy);

        // Act
        var colors = Extract(pixels);

        // Assert
        new[] { colors!.Value.Glow, colors.Value.SecondGlow }.Should().BeEquivalentTo([Orange, Navy]);
    }

    [Fact]
    public void Stray_pixels_are_not_a_glow()
    {
        // Arrange: grey with a single orange pixel
        var pixels = Image((x, y) => x == 30 && y == 45 ? Orange : Grey);

        // Act
        var colors = Extract(pixels);

        // Assert
        colors!.Value.Glow.Should().Be(Grey);
    }

    [Fact]
    public void Transparent_pixels_are_ignored()
    {
        // Arrange: an icon: orange in the middle, transparent around it
        var pixels = Image((x, y) => x is > 20 and < 40 && y is > 30 and < 60 ? Orange : Color.FromArgb(0, 255, 255, 255));

        // Act
        var colors = Extract(pixels);

        // Assert
        colors!.Value.Dominant.Should().Be(Orange);
    }

    [Fact]
    public void Fully_transparent_image_has_no_colors()
    {
        // Arrange
        var pixels = Image((_, _) => Colors.Transparent);

        // Act
        var colors = Extract(pixels);

        // Assert
        colors.Should().BeNull();
    }

    [Fact]
    public void Rgba_byte_order_is_read_too()
    {
        // Arrange: the BGRA buffer of orange read as RGBA is (40, 150, 240)
        var pixels = Image((_, _) => Orange);

        // Act
        var colors = CoverPalette.Extract(pixels, Width, Height, Width * 4, bgra: false);

        // Assert
        colors!.Value.Dominant.Should().Be(Color.FromRgb(40, 150, 240));
    }

    [Fact]
    public void Buffer_smaller_than_the_image_throws()
    {
        // Arrange
        var pixels = new byte[10];

        // Act
        var act = () => CoverPalette.Extract(pixels, Width, Height, Width * 4);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Game_without_cover_uses_its_placeholder_colors()
    {
        // Arrange
        var id = GameId.Steam(10);
        var (top, bottom) = PlaceholderColors.For(id);

        // Act
        var colors = CoverPalette.ForPlaceholder(id);

        // Assert
        colors.Should().Be(new CoverColors(bottom, top, top));
    }
}
