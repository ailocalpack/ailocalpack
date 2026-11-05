using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiLocalPack.Desktop.Covers;

/// <summary>Reads a decoded bitmap as plain pixels, and the greyscale cover of a blocked game (design §3a).</summary>
public static class BitmapPixels
{
    /// <summary>The pixels as BGRA, unpremultiplied, rows of <c>width × 4</c> bytes (Avalonia transcodes); <c>null</c> for an empty or unreadable image.</summary>
    public static byte[]? ReadBgra(Bitmap bitmap)
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
            return buffer;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A grey copy of <paramref name="bitmap"/> (luma, alpha kept); <c>null</c> when it can't be read. Safe off the UI thread.</summary>
    public static Bitmap? Greyscale(Bitmap bitmap)
    {
        if (ReadBgra(bitmap) is not { } buffer)
        {
            return null;
        }

        ToGreyscale(buffer);
        var size = bitmap.PixelSize;
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            // The constructor copies the pixels.
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(), size, bitmap.Dpi, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Turns BGRA pixels grey in place (luma, alpha kept). Pure.</summary>
    public static void ToGreyscale(Span<byte> bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var luma = Luma(bgra[i + 2], bgra[i + 1], bgra[i]);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = luma;
        }
    }

    /// <summary>The grey of the same brightness (alpha kept).</summary>
    public static Color Greyscale(Color color)
    {
        var luma = Luma(color.R, color.G, color.B);
        return Color.FromArgb(color.A, luma, luma, luma);
    }

    /// <summary>Rec. 601 luma, the usual "how bright does it look".</summary>
    private static byte Luma(byte r, byte g, byte b) => (byte)(((299 * r) + (587 * g) + (114 * b) + 500) / 1000);

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
