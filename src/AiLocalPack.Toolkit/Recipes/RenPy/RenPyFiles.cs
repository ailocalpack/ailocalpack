using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>A Ren'Py file can't be read: damaged, or a format (or obfuscation) the toolkit doesn't know.</summary>
internal sealed class RenPyFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A Ren'Py archive (<c>.rpa</c>): <c>RPA-3.0 &lt;index offset&gt; &lt;key&gt;</c> (offsets and lengths XOR the key)
/// or <c>RPA-2.0 &lt;index offset&gt;</c>, the index being a zlib-compressed pickle of
/// <c>{name: [(offset, length[, prefix])]}</c>. Other headers (custom obfuscation) are refused.
/// </summary>
internal sealed class RenPyArchive
{
    private const int HeaderLength = 64;

    private readonly string _path;
    private readonly Dictionary<string, List<(long Offset, long Length, byte[] Prefix)>> _index;

    private RenPyArchive(string path, Dictionary<string, List<(long, long, byte[])>> index)
    {
        _path = path;
        _index = index;
    }

    /// <summary>Entry names (<c>/</c>-separated, as Ren'Py stores them), ordinal order.</summary>
    public IReadOnlyList<string> Names => [.. _index.Keys.Order(StringComparer.Ordinal)];

    /// <exception cref="RenPyFormatException">Not an archive this toolkit reads.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    public static RenPyArchive Open(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[HeaderLength];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var line = Encoding.ASCII.GetString(header, 0, read);
        var end = line.IndexOf('\n', StringComparison.Ordinal);
        var parts = (end < 0 ? line : line[..end]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long offset;
        long key;
        switch (parts)
        {
            case ["RPA-3.0", var o, var k, ..] when TryHex(o, out offset) && TryHex(k, out key):
                break;
            case ["RPA-2.0", var o, ..] when TryHex(o, out offset):
                key = 0;
                break;
            case ["RPA-3.0" or "RPA-2.0", ..]:
                throw new RenPyFormatException("The archive header is damaged.");
            default:
                throw new RenPyFormatException($"Unknown archive format '{(parts.Length > 0 ? parts[0] : string.Empty)}'.");
        }

        if (offset <= 0 || offset >= stream.Length)
        {
            throw new RenPyFormatException("The archive index is outside the file.");
        }

        stream.Seek(offset, SeekOrigin.Begin);
        object? data;
        try
        {
            data = PickleReader.Read(RenPyScriptFile.Inflate(stream), bytesAsLatin1: true);
        }
        catch (Exception e) when (e is PickleException or InvalidDataException)
        {
            throw new RenPyFormatException($"The archive index can't be read: {e.Message}", e);
        }

        if (data is not PyDict entries)
        {
            throw new RenPyFormatException("The archive index is not a dict.");
        }

        var index = new Dictionary<string, List<(long, long, byte[])>>(StringComparer.Ordinal);
        foreach (var (name, value) in entries.Items)
        {
            if (name is not string latin || value is not List<object?> chunks)
            {
                continue;
            }

            var list = new List<(long, long, byte[])>();
            foreach (var chunk in chunks)
            {
                if (chunk is PyTuple { Count: >= 2 } t && t[0] is long o && t[1] is long l)
                {
                    var prefix = t.Count > 2 ? t[2] switch
                    {
                        string s => Encoding.Latin1.GetBytes(s),
                        byte[] b => b,
                        _ => [],
                    } : [];
                    list.Add((o ^ key, l ^ key, prefix));
                }
            }

            index[Name(latin)] = list;
        }

        return new RenPyArchive(path, index);
    }

    /// <summary>The bytes of an entry (as Ren'Py reads it: prefix, then the stored bytes after it).</summary>
    /// <exception cref="RenPyFormatException">The entry lies outside the archive.</exception>
    public byte[] Read(string name)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var result = new MemoryStream();
        foreach (var (offset, length, prefix) in _index[name])
        {
            var stored = length - prefix.Length;
            if (offset < 0 || stored < 0 || offset + stored > stream.Length)
            {
                throw new RenPyFormatException($"{name} lies outside the archive.");
            }

            result.Write(prefix);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[stored];
            stream.ReadExactly(buffer);
            result.Write(buffer);
        }

        return result.ToArray();
    }

    /// <summary>Writes an <c>RPA-3.0</c> archive (synthetic games).</summary>
    public static void Write(string path, IReadOnlyDictionary<string, byte[]> files, long key = 0x42424242)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(new byte[34]);
        var index = new PyDict();
        foreach (var (name, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var offset = stream.Position;
            stream.Write(content);
            index.Set(name, new List<object?> { new PyTuple([offset ^ key, content.Length ^ key, string.Empty]) });
        }

        var indexOffset = stream.Position;
        using (var zlib = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(PickleWriter.Write(index));
        }

        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(Encoding.ASCII.GetBytes($"RPA-3.0 {indexOffset:x16} {key:x8}\n"));
    }

    /// <summary>Python 2 wrote names as UTF-8 byte strings (read as Latin-1 here); Python 3 as text.</summary>
    private static string Name(string latin)
    {
        if (latin.All(c => c < 0x100))
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(Encoding.Latin1.GetBytes(latin)).Replace('\\', '/');
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return latin.Replace('\\', '/');
    }

    private static bool TryHex(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// A compiled Ren'Py script (<c>.rpyc</c>): <c>RENPY RPC2</c> + slot table (<c>slot, start, length</c> as little-endian
/// uint32, ending with slot 0) + zlib-compressed pickles of <c>(data, statements)</c>; slot 1 = as parsed, slot 2 (newer
/// Ren'Py) = after the static transforms, translation blocks included. Older files are one zlib stream (slot 1).
/// </summary>
internal static class RenPyScriptFile
{
    /// <summary>Most bytes one inflated pickle may have (a guard against crafted files).</summary>
    public const int MaxInflated = 512 * 1024 * 1024;

    private static readonly byte[] Header = "RENPY RPC2"u8.ToArray();

    /// <summary>The inflated pickle of <paramref name="slot"/>, or <c>null</c> when the file doesn't have it.</summary>
    /// <exception cref="RenPyFormatException">Damaged.</exception>
    public static byte[]? ReadSlot(byte[] file, int slot)
    {
        try
        {
            if (!file.AsSpan().StartsWith(Header))
            {
                return slot == 1 ? Inflate(new MemoryStream(file, writable: false)) : null;
            }

            for (var pos = Header.Length; pos + 12 <= file.Length; pos += 12)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
                if (id == 0)
                {
                    return null;
                }

                if (id != slot)
                {
                    continue;
                }

                var start = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 8));
                if (start + (long)length > file.Length)
                {
                    throw new RenPyFormatException($"Slot {slot} lies outside the file.");
                }

                return Inflate(new MemoryStream(file, (int)start, (int)length, writable: false));
            }

            throw new RenPyFormatException("The slot table is truncated.");
        }
        catch (InvalidDataException e)
        {
            throw new RenPyFormatException($"Slot {slot} isn't a zlib stream: {e.Message}", e);
        }
    }

    /// <summary>Writes a <c>RENPY RPC2</c> file with the given slots (synthetic games).</summary>
    public static byte[] Write(IReadOnlyDictionary<int, byte[]> slots)
    {
        var compressed = slots.OrderBy(s => s.Key).Select(s => (s.Key, Data: Deflate(s.Value))).ToList();
        using var stream = new MemoryStream();
        stream.Write(Header);
        var start = Header.Length + (12 * (compressed.Count + 1));
        Span<byte> entry = stackalloc byte[12];
        foreach (var (slot, data) in compressed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)slot);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)start);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)data.Length);
            stream.Write(entry);
            start += data.Length;
        }

        stream.Write(new byte[12]);
        foreach (var (_, data) in compressed)
        {
            stream.Write(data);
        }

        return stream.ToArray();
    }

    /// <exception cref="InvalidDataException">Not zlib, or larger than <see cref="MaxInflated"/>.</exception>
    public static byte[] Inflate(Stream compressed)
    {
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            if (result.Length + read > MaxInflated)
            {
                throw new InvalidDataException("The data is too large.");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var result = new MemoryStream();
        using (var zlib = new ZLibStream(result, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return result.ToArray();
    }
}
