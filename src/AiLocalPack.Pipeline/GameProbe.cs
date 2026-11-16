using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline;

/// <summary>
/// What the test run can see of a running game besides its output: how much it has read from disk (a game loading
/// reads a lot, a game sitting in its menu next to nothing), whether it has a window, and a picture of that window.
/// Every answer is best effort: <c>null</c> = this OS (or this game) doesn't tell.
/// </summary>
public interface IGameProbe
{
    /// <summary>Whether this probe can take pictures of a window on this machine (otherwise readiness goes by disk reads alone).</summary>
    bool CanCapture { get; }

    /// <summary>Bytes the process (and the processes it started) read so far, or <c>null</c> when unknown.</summary>
    long? ReadBytes(int processId);

    /// <summary>The game's largest visible window as a picture, or <c>null</c> when it has none (yet) or it can't be taken.</summary>
    GameFrame? Capture(int processId);
}

/// <summary>A picture of the game's window: <paramref name="Pixels"/> = BGRA rows, top first.</summary>
public sealed record GameFrame(int Width, int Height, byte[] Pixels)
{
    /// <summary>
    /// A frame with something on it: not one colour (a black window still starting, a white one hung) — the brightness of
    /// a 32 × 32 grid of points spreads by more than a few steps. A loading screen counts; readiness also waits for the
    /// disk reads to calm down.
    /// </summary>
    public bool HasContent
    {
        get
        {
            if (Width < 8 || Height < 8 || Pixels.Length < Width * Height * 4)
            {
                return false;
            }

            const int grid = 32;
            var values = new List<double>(grid * grid);
            for (var gy = 0; gy < grid; gy++)
            {
                for (var gx = 0; gx < grid; gx++)
                {
                    var x = (gx * 2 + 1) * Width / (grid * 2);
                    var y = (gy * 2 + 1) * Height / (grid * 2);
                    var i = (y * Width + x) * 4;
                    values.Add(0.114 * Pixels[i] + 0.587 * Pixels[i + 1] + 0.299 * Pixels[i + 2]);
                }
            }

            var mean = values.Average();
            var spread = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
            return values.Max() - values.Min() > 24 && spread > 6;
        }
    }

    /// <summary>Writes the frame as a PNG, scaled down to at most <paramref name="maxWidth"/> pixels wide.</summary>
    /// <exception cref="IOException">The file can't be written.</exception>
    public void SavePng(string path, int maxWidth = 1280)
    {
        var scale = Width > maxWidth ? (double)maxWidth / Width : 1;
        var w = Math.Max(1, (int)(Width * scale));
        var h = Math.Max(1, (int)(Height * scale));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        Png.Write(file, w, h, (x, y) =>
        {
            var sx = Math.Min(Width - 1, (int)(x / scale));
            var sy = Math.Min(Height - 1, (int)(y / scale));
            var i = (sy * Width + sx) * 4;
            return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
        });
    }
}

/// <summary>A minimal PNG writer (8-bit RGB, no filter), so screenshots need no imaging library.</summary>
internal static class Png
{
    private static readonly uint[] CrcTable = MakeTable();

    public static void Write(Stream output, int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(output, "IHDR", header);
        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = pixel(x, y);
                    row[1 + x * 3] = r;
                    row[2 + x * 3] = g;
                    row[3 + x * 3] = b;
                }

                zlib.Write(row);
            }
        }

        Chunk(output, "IDAT", data.ToArray());
        Chunk(output, "IEND", []);
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
        output.Write(number);
    }

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>
/// The probe of this OS: Windows (<c>PrintWindow</c>), Linux under X11 (<c>XGetImage</c>; Wayland without X has no
/// pictures, decided: the disk reads alone decide there) and macOS (<c>screencapture -l</c>, with the Screen Recording
/// permission) see the game's window and take its picture; all count disk reads.
/// </summary>
public static class GameProbes
{
    public static IGameProbe For(OsKind os) => os switch
    {
        OsKind.Windows when OperatingSystem.IsWindows() => new WindowsGameProbe(),
        OsKind.Linux when OperatingSystem.IsLinux() => new LinuxGameProbe(),
        OsKind.MacOS when OperatingSystem.IsMacOS() => new MacGameProbe(),
        _ => NoGameProbe.Instance,
    };
}

/// <summary>Sees nothing (tests, an OS mismatch).</summary>
public sealed class NoGameProbe : IGameProbe
{
    public static NoGameProbe Instance { get; } = new();

    public bool CanCapture => false;

    public long? ReadBytes(int processId) => null;

    public GameFrame? Capture(int processId) => null;
}

/// <summary>
/// Windows: the read count of the process and the processes it started (<c>GetProcessIoCounters</c>), their largest
/// visible top-level window, and its picture: <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> (DirectX and OpenGL
/// windows included, even under another window), else the screen under the window.
/// </summary>
internal sealed class WindowsGameProbe : IGameProbe
{
    private const uint PwClientOnly = 1;
    private const uint PwRenderFullContent = 2;
    private const uint SrcCopy = 0x00CC0020;
    private const uint Th32csSnapProcess = 0x2;

    public bool CanCapture => true;

    public long? ReadBytes(int processId)
    {
        long total = 0;
        var any = false;
        foreach (var id in Tree(processId))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (GetProcessIoCounters(process.Handle, out var counters))
                {
                    total += (long)counters.ReadTransferCount;
                    any = true;
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return any ? total : null;
    }

    public GameFrame? Capture(int processId)
    {
        var window = MainWindow(Tree(processId));
        if (window == IntPtr.Zero || !GetClientRect(window, out var rect) || rect.Right - rect.Left < 8 || rect.Bottom - rect.Top < 8)
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var frame = Grab(window, width, height, print: true);
        if (frame is { HasContent: true })
        {
            return frame;
        }

        // Some games draw straight to the screen and PrintWindow gets a black picture: take what the screen shows there.
        return Grab(window, width, height, print: false) ?? frame;
    }

    private static GameFrame? Grab(IntPtr window, int width, int height, bool print)
    {
        var source = GetDC(print ? window : IntPtr.Zero);
        if (source == IntPtr.Zero)
        {
            return null;
        }

        var memory = CreateCompatibleDC(source);
        var bitmap = CreateCompatibleBitmap(source, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            bool ok;
            if (print)
            {
                ok = PrintWindow(window, memory, PwClientOnly | PwRenderFullContent);
            }
            else
            {
                var origin = new Point { X = 0, Y = 0 };
                ok = ClientToScreen(window, ref origin) && BitBlt(memory, 0, 0, width, height, source, origin.X, origin.Y, SrcCopy);
            }

            if (!ok)
            {
                return null;
            }

            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[width * height * 4];
            SelectObject(memory, old);
            return GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == height ? new GameFrame(width, height, pixels) : null;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(print ? window : IntPtr.Zero, source);
        }
    }

    /// <summary>The process and every process it started (the game may be a launcher's child).</summary>
    private static HashSet<int> Tree(int root)
    {
        var tree = new HashSet<int> { root };
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return tree;
        }

        try
        {
            var parents = new List<(int Id, int Parent)>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parents.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                }
                while (Process32Next(snapshot, ref entry));
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
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return tree;
    }

    private static IntPtr MainWindow(HashSet<int> processes)
    {
        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (processes.Contains((int)owner) && IsWindowVisible(window) && GetClientRect(window, out var rect))
            {
                var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = window;
                }
            }

            return true;
        }, IntPtr.Zero);
        return best;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
}
