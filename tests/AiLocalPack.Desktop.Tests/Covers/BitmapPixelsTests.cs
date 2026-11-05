using AiLocalPack.Desktop.Covers;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Tests.Covers;

/// <summary>The grey cover and placeholder of a blocked game (task 23).</summary>
public sealed class BitmapPixelsTests
{
    [Fact]
    public void Greyscale_pixels_keep_their_brightness_and_alpha()
    {
        // Arrange: BGRA opaque red, half-transparent blue
        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 128];

        // Act
        BitmapPixels.ToGreyscale(pixels);

        // Assert
        pixels.Should().Equal(76, 76, 76, 255, 29, 29, 29, 128);
    }

    [Fact]
    public void Greyscale_color_keeps_alpha()
    {
        // Arrange
        var color = Color.FromArgb(128, 0, 255, 0);

        // Act
        var grey = BitmapPixels.Greyscale(color);

        // Assert
        grey.Should().Be(Color.FromArgb(128, 150, 150, 150));
    }
}
