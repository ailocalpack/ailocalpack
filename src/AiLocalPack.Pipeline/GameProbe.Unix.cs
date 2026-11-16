using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Linux: disk reads = <c>/proc/&lt;pid&gt;/io</c>'s <c>rchar</c> (every read, cached or not) summed over the process
/// and the processes it started (parents from <c>/proc/&lt;pid&gt;/stat</c>). Pictures under X11 (<c>DISPLAY</c> set,
/// <c>libX11.so.6</c>): the largest viewable top-level window of the tree in the window manager's
/// <c>_NET_CLIENT_LIST</c>, found by <c>_NET_WM_PID</c>, taken with <c>XGetImage</c>; a GL window a compositor keeps
/// black is taken from the root window where it shows on screen instead. A Wayland session runs X11 games through
/// XWayland, where the same works for the game's own window as far as the compositor allows; a game without an X window,
/// or no X server at all, gives no picture.
/// </summary>
internal sealed class LinuxGameProbe : IGameProbe
{
    private const int ZPixmap = 2;
    private const int IsViewable = 2;
    private const ulong AllPlanes = ulong.MaxValue;

    private static readonly Lazy<bool> HasX = new(() =>
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return false;
        }

        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return false;
            }

            XCloseDisplay(display);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

    public bool CanCapture => HasX.Value;

    public long? ReadBytes(int processId)
    {
        long total = 0;
        var any = false;
        foreach (var id in ProcTree.Of(processId))
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/{id}/io"))
                {
                    if (line.StartsWith("rchar:", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                    {
                        total += bytes;
                        any = true;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return any ? total : null;
    }

    public GameFrame? Capture(int processId)
    {
        if (!CanCapture)
        {
            return null;
        }

        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var root = XDefaultRootWindow(display);
            var (window, width, height) = MainWindow(display, root, ProcTree.Of(processId));
            if (window == 0)
            {
                return null;
            }

            var frame = Grab(display, window, 0, 0, width, height);
            if (frame is { HasContent: true })
            {
                return frame;
            }

            // A GL window the compositor draws itself reads black: take what the screen shows there.
            return XTranslateCoordinates(display, window, root, 0, 0, out var x, out var y, out _) != 0
                ? Grab(display, root, x, y, width, height) ?? frame
                : frame;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static GameFrame? Grab(IntPtr display, ulong drawable, int x, int y, int width, int height)
    {
        var image = XGetImage(display, drawable, x, y, (uint)width, (uint)height, AllPlanes, ZPixmap);
        if (image == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return XImages.ToFrame(image);
        }
        finally
        {
            XImages.Destroy(image);
        }
    }

    private static (ulong Window, int Width, int Height) MainWindow(IntPtr display, ulong root, HashSet<int> processes)
    {
        var clientList = XInternAtom(display, "_NET_CLIENT_LIST", true);
        var pidAtom = XInternAtom(display, "_NET_WM_PID", true);
        if (clientList == 0 || pidAtom == 0)
        {
            return (0, 0, 0);
        }

        (ulong Window, int Width, int Height) best = (0, 0, 0);
        foreach (var window in Longs(display, root, clientList))
        {
            var pid = Longs(display, window, pidAtom).FirstOrDefault();
            if (pid == 0 || pid > int.MaxValue || !processes.Contains((int)pid))
            {
                continue;
            }

            var attributes = new byte[256];
            if (XGetWindowAttributes(display, window, attributes) == 0)
            {
                continue;
            }

            var width = BitConverter.ToInt32(attributes, 8);
            var height = BitConverter.ToInt32(attributes, 12);
            var mapState = BitConverter.ToInt32(attributes, 92);
            if (mapState == IsViewable && width >= 8 && height >= 8 && (long)width * height > (long)best.Width * best.Height)
            {
                best = (window, width, height);
            }
        }

        return best;
    }

    /// <summary>A format-32 property as its values (Xlib hands them out as C longs).</summary>
    private static List<ulong> Longs(IntPtr display, ulong window, ulong property)
    {
        var values = new List<ulong>();
        if (XGetWindowProperty(display, window, property, 0, 4096, false, 0, out _, out var format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
        {
            return values;
        }

        try
        {
            if (format == 32)
            {
                for (var i = 0; i < (int)Math.Min(count, 4096UL); i++)
                {
                    values.Add((ulong)Marshal.ReadInt64(data, i * 8));
                }
            }
        }
        finally
        {
            XFree(data);
        }

        return values;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern ulong XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern ulong XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(
        IntPtr display, ulong window, ulong property, long offset, long length, bool delete, ulong requestedType,
        out ulong actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr data);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(IntPtr display, ulong window, [Out] byte[] attributes);

    [DllImport("libX11.so.6")]
    private static extern int XTranslateCoordinates(IntPtr display, ulong source, ulong destination, int x, int y, out int destinationX, out int destinationY, out ulong child);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetImage(IntPtr display, ulong drawable, int x, int y, uint width, uint height, ulong planeMask, int format);
}

/// <summary>Reading an Xlib <c>XImage</c> (64-bit layout) as a frame.</summary>
internal static class XImages
{
    private const int DataOffset = 16;
    private const int ByteOrderOffset = 24;
    private const int BytesPerLineOffset = 44;
    private const int BitsPerPixelOffset = 48;
    private const int RedMaskOffset = 56;
    private const int DestroyOffset = 96;

    /// <summary>A 24/32-bit image with 8-bit channels as BGRA; <c>null</c> for any other depth or layout.</summary>
    public static GameFrame? ToFrame(IntPtr image)
    {
        var width = Marshal.ReadInt32(image, 0);
        var height = Marshal.ReadInt32(image, 4);
        var data = Marshal.ReadIntPtr(image, DataOffset);
        var lsbFirst = Marshal.ReadInt32(image, ByteOrderOffset) == 0;
        var stride = Marshal.ReadInt32(image, BytesPerLineOffset);
        var bits = Marshal.ReadInt32(image, BitsPerPixelOffset);
        var redMask = (ulong)Marshal.ReadInt64(image, RedMaskOffset);
        if (data == IntPtr.Zero || bits != 32 || width < 1 || height < 1 || stride < width * 4)
        {
            return null;
        }

        var raw = new byte[stride * height];
        Marshal.Copy(data, raw, 0, raw.Length);
        return FromPixels(raw, width, height, stride, lsbFirst, redMask);
    }

    /// <summary>
    /// 32-bit pixels (a stride per row) to BGRA: the usual X layout is a little-endian <c>0x00RRGGBB</c> word, i.e. the
    /// bytes B, G, R, X; a big-endian server or a red mask in the low byte (<c>0x000000FF</c>) are turned around.
    /// </summary>
    internal static GameFrame? FromPixels(byte[] raw, int width, int height, int stride, bool lsbFirst, ulong redMask)
    {
        if (redMask is not (0xFF0000 or 0xFF))
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var word = lsbFirst
                    ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(y * stride + x * 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(y * stride + x * 4));
                var (r, g, b) = redMask == 0xFF0000
                    ? ((byte)(word >> 16), (byte)(word >> 8), (byte)word)
                    : ((byte)word, (byte)(word >> 8), (byte)(word >> 16));
                var i = (y * width + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }

        return new GameFrame(width, height, pixels);
    }

    /// <summary><c>XDestroyImage</c> is a macro over the image's own function.</summary>
    public static void Destroy(IntPtr image)
    {
        var destroy = Marshal.ReadIntPtr(image, DestroyOffset);
        if (destroy != IntPtr.Zero)
        {
            Marshal.GetDelegateForFunctionPointer<DestroyImage>(destroy)(image);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyImage(IntPtr image);
}

/// <summary>A Linux process and every process it started, from the parents in <c>/proc/&lt;pid&gt;/stat</c>.</summary>
internal static class ProcTree
{
    public static HashSet<int> Of(int root)
    {
        var tree = new HashSet<int> { root };
        var parents = new List<(int Id, int Parent)>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                try
                {
                    // "pid (comm) state ppid …": comm may hold spaces and parentheses, so read after the last ')'.
                    var stat = File.ReadAllText(Path.Combine(folder, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
                    {
                        parents.Add((id, parent));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return tree;
        }

        bool grew;
        do
        {
            grew = false;
            foreach (var (id, parent) in parents)
            {
                if (tree.Contains(parent) && tree.Add(id))
                {
                    grew = true;
                }
            }
        }
        while (grew);
        return tree;
    }
}

/// <summary>
/// macOS: disk reads = <c>proc_pid_rusage</c>'s <c>ri_diskio_bytesread</c> (rusage_info_v2) summed over the process
/// and its children (<c>proc_listchildpids</c>). Pictures with the Screen Recording permission
/// (<c>CGPreflightScreenCaptureAccess</c>; without it the first look asks once with <c>CGRequestScreenCaptureAccess</c>
/// and there are no pictures, decided: the disk reads alone decide then): the tree's largest on-screen window of layer 0
/// from <c>CGWindowListCopyWindowInfo</c>, taken with <c>screencapture -x -o -l &lt;window&gt; -t bmp</c> into a
/// temporary file and read back.
/// </summary>
internal sealed class MacGameProbe : IGameProbe
{
    private const int RusageInfoV2 = 2;
    private const int BytesReadOffset = 16 + 16 * 8;
    private const uint OnScreenOnly = 1 << 0;
    private const uint ExcludeDesktopElements = 1 << 4;
    private const int NumberSInt64 = 4;
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    private int _asked;

    public bool CanCapture
    {
        get
        {
            try
            {
                return CGPreflightScreenCaptureAccess();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public long? ReadBytes(int processId)
    {
        long total = 0;
        var any = false;
        foreach (var id in Tree(processId))
        {
            var buffer = new byte[256];
            try
            {
                if (ProcPidRusage(id, RusageInfoV2, buffer) == 0)
                {
                    total += (long)BitConverter.ToUInt64(buffer, BytesReadOffset);
                    any = true;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        return any ? total : null;
    }

    public GameFrame? Capture(int processId)
    {
        if (!CanCapture)
        {
            if (Interlocked.Exchange(ref _asked, 1) == 0)
            {
                try
                {
                    CGRequestScreenCaptureAccess();
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                }
            }

            return null;
        }

        try
        {
            return MainWindow(Tree(processId)) is { } window ? Take(window) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static GameFrame? Take(long window)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ailocalpack-shot-{Environment.ProcessId}-{window}.bmp");
        try
        {
            var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-x", "-o", "-l", window.ToString(CultureInfo.InvariantCulture), "-t", "bmp", file })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(CaptureTimeout))
            {
                process?.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && File.Exists(file) ? Bmp.Read(File.ReadAllBytes(file)) : null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The window number of the tree's largest on-screen normal window, or <c>null</c>.</summary>
    private static long? MainWindow(HashSet<int> processes)
    {
        var list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
        if (list == IntPtr.Zero)
        {
            return null;
        }

        var ownerKey = Key("kCGWindowOwnerPID");
        var numberKey = Key("kCGWindowNumber");
        var layerKey = Key("kCGWindowLayer");
        var boundsKey = Key("kCGWindowBounds");
        try
        {
            long? best = null;
            double bestArea = 0;
            for (long i = 0, count = CFArrayGetCount(list); i < count; i++)
            {
                var entry = CFArrayGetValueAtIndex(list, i);
                if (Number(entry, ownerKey) is not { } owner || !processes.Contains((int)owner) || Number(entry, layerKey) is not 0 || Number(entry, numberKey) is not { } number)
                {
                    continue;
                }

                var bounds = CFDictionaryGetValue(entry, boundsKey);
                if (bounds == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(bounds, out var rect))
                {
                    continue;
                }

                var area = rect.Width * rect.Height;
                if (rect.Width >= 8 && rect.Height >= 8 && area > bestArea)
                {
                    bestArea = area;
                    best = number;
                }
            }

            return best;
        }
        finally
        {
            CFRelease(ownerKey);
            CFRelease(numberKey);
            CFRelease(layerKey);
            CFRelease(boundsKey);
            CFRelease(list);
        }
    }

    private static IntPtr Key(string name) => CFStringCreateWithCString(IntPtr.Zero, name, 0x08000100);

    private static long? Number(IntPtr dictionary, IntPtr key)
    {
        var value = CFDictionaryGetValue(dictionary, key);
        return value != IntPtr.Zero && CFNumberGetValue(value, NumberSInt64, out var number) ? number : null;
    }

    private static HashSet<int> Tree(int root)
    {
        var tree = new HashSet<int> { root };
        var queue = new Queue<int>([root]);
        var buffer = new int[1024];
        while (queue.Count > 0 && tree.Count < 256)
        {
            var parent = queue.Dequeue();
            int found;
            try
            {
                found = ProcListChildPids(parent, buffer, buffer.Length * sizeof(int));
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return tree;
            }

            for (var i = 0; i < Math.Min(found, buffer.Length); i++)
            {
                if (buffer[i] > 0 && tree.Add(buffer[i]))
                {
                    queue.Enqueue(buffer[i]);
                }
            }
        }

        return tree;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage")]
    private static extern int ProcPidRusage(int pid, int flavor, [Out] byte[] buffer);

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listchildpids")]
    private static extern int ProcListChildPids(int pid, [Out] int[] buffer, int bufferSize);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out CGRect rect);

    [DllImport(CoreFoundation)]
    private static extern long CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}

/// <summary>A minimal BMP reader (24 and 32 bits, uncompressed or bit fields, either row order), for macOS screenshots.</summary>
internal static class Bmp
{
    /// <summary>The picture as BGRA, or <c>null</c> for anything else.</summary>
    public static GameFrame? Read(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
        {
            return null;
        }

        var offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(30));
        var height = Math.Abs(rawHeight);
        if (width < 1 || height < 1 || bits is not (24 or 32) || compression is not (0 or 3))
        {
            return null;
        }

        var bytesPerPixel = bits / 8;
        var stride = (width * bytesPerPixel + 3) & ~3;
        if (offset < 0 || (long)offset + (long)stride * height > data.Length)
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = offset + (rawHeight > 0 ? height - 1 - y : y) * stride;
            for (var x = 0; x < width; x++)
            {
                var s = row + x * bytesPerPixel;
                var i = (y * width + x) * 4;
                pixels[i] = data[s];
                pixels[i + 1] = data[s + 1];
                pixels[i + 2] = data[s + 2];
                pixels[i + 3] = 255;
            }
        }

        return new GameFrame(width, height, pixels);
    }
}
