using System.Buffers.Binary;
using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Folders;

/// <summary>Image format of a <see cref="GameIcon"/>.</summary>
public enum GameIconFormat
{
    /// <summary>A PNG file.</summary>
    Png,

    /// <summary>A one-image <c>.ico</c> file (a Windows icon stored as a bitmap, not PNG).</summary>
    Ico,
}

/// <summary>A game's icon as an image file's bytes.</summary>
public sealed record GameIcon(byte[] Data, GameIconFormat Format)
{
    /// <summary>File extension for the format, with the dot.</summary>
    public string Extension => Format == GameIconFormat.Png ? ".png" : ".ico";
}

/// <summary>
/// Finds a game's icon, used as its cover until the player picks an image (design §2). By main executable: a Windows
/// <c>.exe</c> gives its largest group icon (PE resources), a macOS bundle the largest PNG of its <c>.icns</c>
/// (<c>CFBundleIconFile</c> of <c>Info.plist</c>, else the first <c>.icns</c> in <c>Contents/Resources</c>); then, for every
/// game, the <c>Icon=</c> PNG of a <c>.desktop</c> file in the game folder (Linux) and well-known PNGs (Unity
/// <c>&lt;Name&gt;_Data/Resources/UnityPlayer.png</c>, Ren'Py <c>game/gui/window_icon.png</c>, <c>icon.png</c>). The
/// formats are read by hand on every OS, so the result is the same everywhere. Best effort: never throws for disk state;
/// <c>null</c> = no icon (the cover placeholder shows).
/// </summary>
public sealed partial class GameIconExtractor(ILogger<GameIconExtractor>? logger = null)
{
    private const int ResourceTypeIcon = 3;
    private const int ResourceTypeGroupIcon = 14;

    /// <summary>Icons bigger than this are not read (a broken size field must not allocate gigabytes).</summary>
    private const int MaxImageBytes = 16 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly ILogger _logger = logger ?? NullLogger<GameIconExtractor>.Instance;

    /// <param name="gameFolder">The game folder.</param>
    /// <param name="mainExecutable">Main executable relative to it ('/'-separated; a macOS bundle is fine), or <c>null</c>.</param>
    public GameIcon? Extract(string gameFolder, string? mainExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var root = Path.GetFullPath(gameFolder);
        try
        {
            return FromExecutable(root, mainExecutable) ?? FromDesktopFile(root) ?? FromKnownPng(root, mainExecutable);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogInformation("Can't read the icon of {Folder}: {Reason}", root, e.Message);
            return null;
        }
    }

    private GameIcon? FromExecutable(string root, string? mainExecutable)
    {
        if (BundleOf(root, mainExecutable) is { } bundle)
        {
            return FromBundle(bundle);
        }

        if (mainExecutable is not null && mainExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(root, mainExecutable);
            return File.Exists(path) ? FromPe(path) : null;
        }

        return null;
    }

    /// <summary>The macOS bundle the main executable belongs to: a <c>.app</c> in its path, or the game folder itself.</summary>
    private static string? BundleOf(string root, string? mainExecutable)
    {
        if (mainExecutable is not null)
        {
            var parts = mainExecutable.Split('/');
            var app = Array.FindIndex(parts, EngineDetector.IsBundle);
            if (app >= 0)
            {
                return Path.Combine([root, .. parts[..(app + 1)]]);
            }
        }

        return File.Exists(Path.Combine(root, "Contents", "Info.plist")) ? root : null;
    }

    // ---------- macOS: .icns ----------

    private GameIcon? FromBundle(string bundle)
    {
        var resources = Path.Combine(bundle, "Contents", "Resources");
        if (!Directory.Exists(resources))
        {
            return null;
        }

        var candidates = new List<string>();
        if (PlistIconFile(Path.Combine(bundle, "Contents", "Info.plist")) is { } named)
        {
            candidates.Add(Path.Combine(resources, named.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? named : named + ".icns"));
        }

        candidates.AddRange(Directory.EnumerateFiles(resources, "*.icns").Order(StringComparer.Ordinal));
        foreach (var file in candidates.Distinct(StringComparer.Ordinal).Where(File.Exists))
        {
            if (FromIcns(File.ReadAllBytes(file)) is { } png)
            {
                return new GameIcon(png, GameIconFormat.Png);
            }
        }

        return null;
    }

    /// <summary><c>CFBundleIconFile</c> of an XML <c>Info.plist</c> (a binary plist falls back to the <c>.icns</c> search).</summary>
    private static string? PlistIconFile(string plist)
    {
        if (!File.Exists(plist))
        {
            return null;
        }

        var match = PlistIconRegex().Match(File.ReadAllText(plist));
        var name = match.Success ? match.Groups[1].Value.Trim() : null;
        return string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) ? null : name;
    }

    /// <summary>The widest PNG image of an <c>.icns</c> file (entries: 4-byte type, 4-byte big-endian length with the header).</summary>
    public static byte[]? FromIcns(ReadOnlySpan<byte> icns)
    {
        if (icns.Length < 8 || !icns[..4].SequenceEqual("icns"u8))
        {
            return null;
        }

        var total = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(icns[4..]), (uint)icns.Length);
        byte[]? best = null;
        var bestWidth = -1;
        for (var at = 8; at + 8 <= total;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(icns[(at + 4)..]);
            if (length < 8 || length > total - at)
            {
                break;
            }

            var data = icns.Slice(at + 8, (int)length - 8);
            if (PngWidth(data) is { } width && width > bestWidth)
            {
                best = data.ToArray();
                bestWidth = width;
            }

            at += (int)length;
        }

        return best;
    }

    // ---------- Windows: PE resources ----------

    private GameIcon? FromPe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var icon = PeIcon(stream);
        if (icon is null)
        {
            _logger.LogDebug("No icon resource in {Path}", path);
        }

        return icon;
    }

    /// <summary>The largest icon of the first <c>RT_GROUP_ICON</c> of a PE file, or <c>null</c> when there is none or the file is malformed.</summary>
    public static GameIcon? PeIcon(Stream pe)
    {
        ArgumentNullException.ThrowIfNull(pe);
        try
        {
            return ReadPeIcon(new PeReader(pe));
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static GameIcon? ReadPeIcon(PeReader pe)
    {
        if (pe.U16(0) != 0x5A4D)
        {
            return null;
        }

        var header = pe.U32(0x3C);
        if (pe.U32(header) != 0x00004550)
        {
            return null;
        }

        var sections = pe.U16(header + 6);
        var optionalSize = pe.U16(header + 20);
        var optional = header + 24;
        var directories = pe.U16(optional) switch
        {
            0x10B => optional + 96,
            0x20B => optional + 112,
            _ => throw new InvalidDataException("Unknown optional header."),
        };
        var directoryCount = pe.U32(directories - 4);
        if (directoryCount < 3)
        {
            return null;
        }

        var resourceRva = pe.U32(directories + (2 * 8));
        if (resourceRva == 0)
        {
            return null;
        }

        var sectionTable = optional + optionalSize;
        var map = new List<(uint Rva, uint Size, uint Offset)>();
        for (var i = 0; i < sections; i++)
        {
            var s = sectionTable + (i * 40);
            map.Add((pe.U32(s + 12), Math.Max(pe.U32(s + 8), pe.U32(s + 16)), pe.U32(s + 20)));
        }

        long Offset(uint rva)
        {
            foreach (var (start, size, offset) in map)
            {
                if (rva >= start && rva < start + size)
                {
                    return offset + (rva - start);
                }
            }

            throw new InvalidDataException("RVA outside every section.");
        }

        var resources = Offset(resourceRva);
        if (FindResource(pe, resources, ResourceTypeGroupIcon, null) is not { } group)
        {
            return null;
        }

        var groupData = pe.Bytes(Offset(group.Rva), group.Size);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(groupData.AsSpan(4));
        (int Width, int Bits, ushort Id, int Index)? best = null;
        for (var i = 0; i < count && 6 + ((i + 1) * 14) <= groupData.Length; i++)
        {
            var entry = groupData.AsSpan(6 + (i * 14));
            var width = entry[0] == 0 ? 256 : entry[0];
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]);
            if (best is null || width > best.Value.Width || (width == best.Value.Width && bits > best.Value.Bits))
            {
                best = (width, bits, id, i);
            }
        }

        if (best is not { } chosen || FindResource(pe, resources, ResourceTypeIcon, chosen.Id) is not { } image)
        {
            return null;
        }

        var data = pe.Bytes(Offset(image.Rva), image.Size);
        if (data.AsSpan().StartsWith(PngSignature))
        {
            return new GameIcon(data, GameIconFormat.Png);
        }

        // A bitmap icon: wrap the group entry and its image into a one-image .ico file.
        var ico = new byte[22 + data.Length];
        ico[2] = 1;
        ico[4] = 1;
        groupData.AsSpan(6 + (chosen.Index * 14), 8).CopyTo(ico.AsSpan(6));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        data.CopyTo(ico.AsSpan(22));
        return new GameIcon(ico, GameIconFormat.Ico);
    }

    /// <summary>Data entry (RVA, size) of resource <paramref name="type"/> / <paramref name="id"/> (first one when <c>null</c>) / first language.</summary>
    private static (uint Rva, int Size)? FindResource(PeReader pe, long root, int type, int? id)
    {
        if (Entry(pe, root, type) is not { } typeDir || !typeDir.IsDirectory)
        {
            return null;
        }

        if (Entry(pe, root + typeDir.Offset, id) is not { } nameDir || !nameDir.IsDirectory)
        {
            return null;
        }

        if (Entry(pe, root + nameDir.Offset, null) is not { } language || language.IsDirectory)
        {
            return null;
        }

        var data = root + language.Offset;
        var size = pe.U32(data + 4);
        return size is 0 or > MaxImageBytes ? null : (pe.U32(data), (int)size);
    }

    /// <summary>The entry with integer ID <paramref name="id"/> (or the first entry, named or not, when <c>null</c>) of a resource directory.</summary>
    private static (bool IsDirectory, uint Offset)? Entry(PeReader pe, long directory, int? id)
    {
        var named = pe.U16(directory + 12);
        var ids = pe.U16(directory + 14);
        for (var i = 0; i < named + ids; i++)
        {
            var entry = directory + 16 + (i * 8);
            var name = pe.U32(entry);
            var target = pe.U32(entry + 4);
            if (id is null || (i >= named && name == id))
            {
                return ((target & 0x80000000) != 0, target & 0x7FFFFFFF);
            }
        }

        return null;
    }

    // ---------- Linux: .desktop files and PNGs ----------

    private static GameIcon? FromDesktopFile(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.desktop").Order(StringComparer.Ordinal))
        {
            var icon = File.ReadLines(file)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("Icon=", StringComparison.Ordinal))
                .Select(l => l["Icon=".Length..].Trim())
                .FirstOrDefault();
            if (string.IsNullOrEmpty(icon))
            {
                continue;
            }

            // A theme icon name ("steam_icon_123") can't be resolved without the desktop's icon theme; paths can.
            var path = Path.IsPathRooted(icon) ? icon : Path.Combine(root, icon);
            if (ReadPng(path) is { } png)
            {
                return png;
            }
        }

        return null;
    }

    private static GameIcon? FromKnownPng(string root, string? mainExecutable)
    {
        var candidates = new List<string>();
        foreach (var data in Directory.EnumerateDirectories(root, "*_Data").Order(StringComparer.Ordinal))
        {
            candidates.Add(Path.Combine(data, "Resources", "UnityPlayer.png"));
        }

        candidates.Add(Path.Combine(root, "game", "gui", "window_icon.png"));
        if (mainExecutable is not null)
        {
            var stem = Path.GetFileNameWithoutExtension(mainExecutable.Split('/')[^1]);
            candidates.Add(Path.Combine(root, stem + ".png"));
        }

        candidates.Add(Path.Combine(root, "icon.png"));
        return candidates.Select(ReadPng).FirstOrDefault(p => p is not null);
    }

    private static GameIcon? ReadPng(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length > MaxImageBytes)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return PngWidth(bytes) is null ? null : new GameIcon(bytes, GameIconFormat.Png);
    }

    /// <summary>Width from the IHDR chunk of a PNG, or <c>null</c> when <paramref name="data"/> is not a PNG.</summary>
    private static int? PngWidth(ReadOnlySpan<byte> data) =>
        data.Length >= 24 && data.StartsWith(PngSignature) && data[12..16].SequenceEqual("IHDR"u8)
            ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), int.MaxValue)
            : null;

    [GeneratedRegex(@"<key>\s*CFBundleIconFile\s*</key>\s*<string>([^<]*)</string>", RegexOptions.CultureInvariant)]
    private static partial Regex PlistIconRegex();

    /// <summary>Little-endian reads at absolute offsets of a seekable stream; reading past the end throws <see cref="EndOfStreamException"/>.</summary>
    private sealed class PeReader(Stream stream)
    {
        public ushort U16(long offset) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(offset, 2));

        public uint U32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(offset, 4));

        public byte[] Bytes(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > stream.Length)
            {
                throw new EndOfStreamException();
            }

            stream.Position = offset;
            var buffer = new byte[count];
            stream.ReadExactly(buffer);
            return buffer;
        }
    }
}
