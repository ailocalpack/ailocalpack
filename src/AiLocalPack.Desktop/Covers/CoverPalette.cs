using System.Runtime.InteropServices;
using AiLocalPack.Core.Games;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiLocalPack.Desktop.Covers;

/// <summary>
/// Colors of a cover for the game page's backdrop (design §3): <paramref name="Dominant"/> = the most common color,
/// <paramref name="Glow"/> and <paramref name="SecondGlow"/> = two distinct, preferably vivid colors of it.
/// </summary>
public readonly record struct CoverColors(Color Dominant, Color Glow, Color SecondGlow);

/// <summary>
/// Dominant-color extraction: pixels are sampled (about 4 000), opaque ones are bucketed by their top 4 bits per channel,
/// the biggest bucket is the dominant color and the glows are the buckets that score best on size × vividness, each far
/// enough from the ones already picked. Pure over a pixel buffer; <see cref="FromBitmap"/> reads a decoded cover.
/// </summary>
public static class CoverPalette
{
    private const int TargetSamples = 4096;

    /// <summary>A bucket must hold this share of the samples to be a glow (stray pixels and JPEG noise aren't).</summary>
    private const double MinGlowShare = 0.02;

    /// <summary>Squared RGB distance two picked colors keep at least.</summary>
    private const int MinDistanceSquared = 64 * 64;

    /// <summary>Colors of a 32-bit pixel buffer; <c>null</c> when no pixel is opaque enough.</summary>
    /// <param name="pixels">Rows of <paramref name="width"/> 4-byte pixels, <paramref name="stride"/> bytes apart.</param>
    /// <param name="bgra">Byte order: B, G, R, A (else R, G, B, A).</param>
    public static CoverColors? Extract(ReadOnlySpan<byte> pixels, int width, int height, int stride, bool bgra = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if (pixels.Length < (stride * (height - 1)) + (width * 4))
        {
            throw new ArgumentException("The buffer is smaller than the image.", nameof(pixels));
        }

        var step = Math.Max(1, (int)Math.Sqrt((double)width * height / TargetSamples));
        var buckets = new Dictionary<int, Bucket>();
        var samples = 0;
        for (var y = step / 2; y < height; y += step)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (var x = step / 2; x < width; x += step)
            {
                var p = row.Slice(x * 4, 4);
                if (p[3] < 128)
                {
                    continue;
                }

                var (r, g, b) = bgra ? (p[2], p[1], p[0]) : (p[0], p[1], p[2]);
                var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                buckets[key] = buckets.GetValueOrDefault(key).Add(r, g, b);
                samples++;
            }
        }

        if (samples == 0)
        {
            return null;
        }

        var ranked = buckets.Values.OrderByDescending(b => b.Count).Select(b => b.Average()).ToList();
        var dominant = ranked[0];
        var vivid = buckets.Values
            .Where(b => b.Count >= samples * MinGlowShare)
            .OrderByDescending(b => b.Count * (0.25 + Saturation(b.Average())))
            .Select(b => b.Average())
            .ToList();

        var glow = vivid.FirstOrDefault(c => Saturation(c) > Saturation(dominant) && Far(c, dominant), dominant);
        var second = vivid.Concat(ranked).FirstOrDefault(c => Far(c, glow) && (Far(c, dominant) || glow == dominant), glow);
        return new CoverColors(dominant, glow, second);
    }

    /// <summary>Colors of a decoded cover (transcoded to BGRA, whatever its own format); <c>null</c> for an empty image or one it can't read.</summary>
    public static CoverColors? FromBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var size = bitmap.PixelSize;
        if (size.Width == 0 || size.Height == 0)
        {
            return null;
        }

        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new BufferFramebuffer(handle.AddrOfPinnedObject(), size, stride, bitmap.Dpi));
        }
        catch (NotSupportedException)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }

        return Extract(buffer, size.Width, size.Height, stride);
    }

    /// <summary>The backdrop colors of a game without cover art: its placeholder gradient.</summary>
    public static CoverColors ForPlaceholder(GameId id)
    {
        var (top, bottom) = PlaceholderColors.For(id);
        return new CoverColors(bottom, top, top);
    }

    /// <summary>0 (grey) … 1 (pure hue), HSV saturation.</summary>
    public static double Saturation(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max == 0 ? 0 : (max - min) / (double)max;
    }

    private static bool Far(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return (dr * dr) + (dg * dg) + (db * db) >= MinDistanceSquared;
    }

    private readonly record struct Bucket(int Count, long R, long G, long B)
    {
        public Bucket Add(byte r, byte g, byte b) => new(Count + 1, R + r, G + g, B + b);

        public Color Average() => Color.FromRgb((byte)(R / Count), (byte)(G / Count), (byte)(B / Count));
    }

    /// <summary>A plain BGRA (unpremultiplied) buffer to copy a bitmap into; Avalonia transcodes to it.</summary>
    private sealed class BufferFramebuffer(IntPtr address, PixelSize size, int rowBytes, Vector dpi) : ILockedFramebuffer
    {
        public IntPtr Address { get; } = address;

        public PixelSize Size { get; } = size;

        public int RowBytes { get; } = rowBytes;

        public Vector Dpi { get; } = dpi;

        public PixelFormat Format => PixelFormat.Bgra8888;

        public AlphaFormat AlphaFormat => AlphaFormat.Unpremul;

        public void Dispose()
        {
        }
    }
}
