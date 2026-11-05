using AiLocalPack.Core.Games;
using Avalonia;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Covers;

/// <summary>Placeholder cover gradients (top, bottom), picked by a stable hash of the game ID so a game keeps its colors.</summary>
public static class PlaceholderColors
{
    private static readonly (Color Top, Color Bottom)[] Gradients =
    [
        (Color.Parse("#3b4a8c"), Color.Parse("#1b2140")),
        (Color.Parse("#2f6f73"), Color.Parse("#132c33")),
        (Color.Parse("#7a3f6b"), Color.Parse("#2c1530")),
        (Color.Parse("#7a5a2c"), Color.Parse("#2e2112")),
        (Color.Parse("#3f6b3a"), Color.Parse("#16271a")),
        (Color.Parse("#5b4a8c"), Color.Parse("#221a3c")),
    ];

    public static (Color Top, Color Bottom) For(GameId id) => Gradients[StableHash(id.ToString()) % (uint)Gradients.Length];

    /// <summary>The placeholder cover: a diagonal gradient. Avalonia object: create it on the UI thread.</summary>
    /// <param name="greyscale">Grey, for a blocked game's page (design §3a).</param>
    public static IBrush CreateBrush(GameId id, bool greyscale = false)
    {
        var (top, bottom) = For(id);
        if (greyscale)
        {
            (top, bottom) = (BitmapPixels.Greyscale(top), BitmapPixels.Greyscale(bottom));
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        }.ToImmutable();
    }

    /// <summary>FNV-1a: <see cref="string.GetHashCode()"/> differs between runs, the placeholder color must not.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
