using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AiLocalPack.Toolkit.Fonts;

/// <summary>
/// Writes small synthetic fonts for the fixture games and tests (never a real font: no outlines, empty glyphs): a
/// TrueType file whose <c>cmap</c> maps the given characters (format 4, plus format 12 for characters beyond the BMP)
/// and whose <c>name</c> gives the family, and the same font wrapped as WOFF, WOFF2 or a one-font collection.
/// </summary>
public static class FontWriter
{
    /// <summary>Printable ASCII: what a Latin-only game font has at least.</summary>
    public static IReadOnlyList<int> Ascii { get; } = [.. Enumerable.Range(0x20, 0x5F)];

    /// <summary>A TrueType font mapping <paramref name="characters"/> to glyphs 1…N.</summary>
    public static byte[] TrueType(string family, IEnumerable<int> characters)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        ArgumentNullException.ThrowIfNull(characters);
        var mapped = characters.Distinct().Order().ToList();
        var glyphs = mapped.Count + 1;
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(mapped),
            ["head"] = Head(),
            ["hhea"] = Hhea(glyphs),
            ["maxp"] = Maxp(glyphs),
            ["name"] = Name(family),
            ["post"] = Post(),
        };
        return Sfnt(0x00010000, tables);
    }

    /// <summary><paramref name="font"/> (a TrueType file) wrapped as WOFF 1.0, each table zlib-compressed.</summary>
    public static byte[] Woff(byte[] font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var tables = Tables(font);
        using var body = new MemoryStream();
        var directory = new List<(uint Tag, int Offset, int Compressed, int Length, uint Checksum)>();
        var headerSize = 44 + (tables.Count * 20);
        foreach (var (tag, data) in tables)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data);
            }

            var stored = compressed.Length < data.Length ? compressed.ToArray() : data;
            directory.Add((Tag(tag), headerSize + (int)body.Length, stored.Length, data.Length, Checksum(data)));
            body.Write(stored);
            Pad(body);
        }

        var result = new byte[headerSize + body.Length];
        var span = result.AsSpan();
        Put32(span, 0, 0x774F4646);
        Put32(span, 4, 0x00010000);
        Put32(span, 8, (uint)result.Length);
        Put16(span, 12, tables.Count);
        Put32(span, 16, (uint)font.Length);
        for (var i = 0; i < directory.Count; i++)
        {
            var at = 44 + (i * 20);
            Put32(span, at, directory[i].Tag);
            Put32(span, at + 4, (uint)directory[i].Offset);
            Put32(span, at + 8, (uint)directory[i].Compressed);
            Put32(span, at + 12, (uint)directory[i].Length);
            Put32(span, at + 16, directory[i].Checksum);
        }

        body.ToArray().CopyTo(result, headerSize);
        return result;
    }

    /// <summary><paramref name="font"/> (a TrueType file) wrapped as WOFF2: every table untransformed, one Brotli stream.</summary>
    public static byte[] Woff2(byte[] font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var tables = Tables(font);
        using var directory = new MemoryStream();
        using var raw = new MemoryStream();
        foreach (var (tag, data) in tables)
        {
            // Tag index 63 + the tag; transform version 0 (glyf / loca: 3 = untransformed, but there are none).
            directory.WriteByte(63);
            directory.Write(Encoding.ASCII.GetBytes(tag));
            WriteBase128(directory, (uint)data.Length);
            raw.Write(data);
        }

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(brotli);
        }

        var header = new byte[48];
        var size = 48 + directory.Length + compressed.Length;
        Put32(header, 0, 0x774F4632);
        Put32(header, 4, 0x00010000);
        Put32(header, 8, (uint)size);
        Put16(header, 12, tables.Count);
        Put32(header, 16, (uint)font.Length);
        Put32(header, 20, (uint)compressed.Length);
        return [.. header, .. directory.ToArray(), .. compressed.ToArray()];
    }

    /// <summary><paramref name="fonts"/> (TrueType files) as one collection (<c>ttcf</c> 1.0).</summary>
    public static byte[] Collection(params byte[][] fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        using var output = new MemoryStream();
        var header = 12 + (fonts.Length * 4);
        output.Write(new byte[header]);
        var offsets = new List<uint>();
        foreach (var font in fonts)
        {
            var copy = (byte[])font.Clone();
            var start = (uint)output.Length;
            offsets.Add(start);
            var count = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(4));
            for (var i = 0; i < count; i++)
            {
                // Table offsets in a collection count from the start of the file.
                var at = 12 + (i * 16) + 8;
                Put32(copy, at, BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(at)) + start);
            }

            output.Write(copy);
            Pad(output);
        }

        var result = output.ToArray();
        Put32(result, 0, 0x74746366);
        Put32(result, 4, 0x00010000);
        Put32(result, 8, (uint)fonts.Length);
        for (var i = 0; i < offsets.Count; i++)
        {
            Put32(result, 12 + (i * 4), offsets[i]);
        }

        return result;
    }

    private static List<(string Tag, byte[] Data)> Tables(byte[] font)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        var tables = new List<(string, byte[])>();
        for (var i = 0; i < count; i++)
        {
            var at = 12 + (i * 16);
            var tag = Encoding.ASCII.GetString(font, at, 4);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at + 8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at + 12));
            tables.Add((tag, font.AsSpan(offset, length).ToArray()));
        }

        return tables;
    }

    private static byte[] Sfnt(uint version, SortedDictionary<string, byte[]> tables)
    {
        using var output = new MemoryStream();
        var header = 12 + (tables.Count * 16);
        output.Write(new byte[header]);
        var records = new List<(string Tag, uint Offset, int Length, uint Checksum)>();
        foreach (var (tag, data) in tables)
        {
            records.Add((tag, (uint)output.Length, data.Length, Checksum(data)));
            output.Write(data);
            Pad(output);
        }

        var result = output.ToArray();
        Put32(result, 0, version);
        Put16(result, 4, tables.Count);
        var power = 1;
        var log = 0;
        while (power * 2 <= tables.Count)
        {
            power *= 2;
            log++;
        }

        Put16(result, 6, power * 16);
        Put16(result, 8, log);
        Put16(result, 10, (tables.Count * 16) - (power * 16));
        for (var i = 0; i < records.Count; i++)
        {
            var at = 12 + (i * 16);
            Put32(result, at, Tag(records[i].Tag));
            Put32(result, at + 4, records[i].Checksum);
            Put32(result, at + 8, records[i].Offset);
            Put32(result, at + 12, (uint)records[i].Length);
        }

        return result;
    }

    private static byte[] Cmap(List<int> characters)
    {
        var bmp = characters.Where(c => c <= 0xFFFF).ToList();
        var format4 = Format4(bmp);
        var format12 = characters.Any(c => c > 0xFFFF) ? Format12(characters) : null;
        using var output = new MemoryStream();
        var records = format12 is null ? 1 : 2;
        var header = new byte[4 + (records * 8)];
        Put16(header, 2, records);
        Put16(header, 4, 3);
        Put16(header, 6, 1);
        Put32(header, 8, (uint)header.Length);
        if (format12 is not null)
        {
            Put16(header, 12, 3);
            Put16(header, 14, 10);
            Put32(header, 16, (uint)(header.Length + format4.Length));
        }

        output.Write(header);
        output.Write(format4);
        if (format12 is not null)
        {
            output.Write(format12);
        }

        return output.ToArray();
    }

    /// <summary>One segment per run of consecutive characters (idDelta maps them to their glyph IDs), then the 0xFFFF end.</summary>
    private static byte[] Format4(List<int> bmp)
    {
        // Characters are sorted and glyph IDs follow them, so glyph = index + 1.
        var segments = new List<(int Start, int End, int Delta)>();
        for (var i = 0; i < bmp.Count; i++)
        {
            var c = bmp[i];
            var delta = (i + 1 - c) & 0xFFFF;
            if (segments.Count > 0 && segments[^1].End == c - 1 && segments[^1].Delta == delta)
            {
                segments[^1] = (segments[^1].Start, c, delta);
            }
            else
            {
                segments.Add((c, c, delta));
            }
        }

        segments.Add((0xFFFF, 0xFFFF, 1));
        var count = segments.Count;
        var length = 16 + (count * 8);
        var data = new byte[length];
        Put16(data, 0, 4);
        Put16(data, 2, length);
        Put16(data, 6, count * 2);
        for (var i = 0; i < count; i++)
        {
            Put16(data, 14 + (i * 2), segments[i].End);
            Put16(data, 16 + (count * 2) + (i * 2), segments[i].Start);
            Put16(data, 16 + (count * 4) + (i * 2), segments[i].Delta);
        }

        return data;
    }

    private static byte[] Format12(List<int> characters)
    {
        var data = new byte[16 + (characters.Count * 12)];
        Put16(data, 0, 12);
        Put32(data, 4, (uint)data.Length);
        Put32(data, 12, (uint)characters.Count);
        for (var i = 0; i < characters.Count; i++)
        {
            var at = 16 + (i * 12);
            Put32(data, at, (uint)characters[i]);
            Put32(data, at + 4, (uint)characters[i]);
            Put32(data, at + 8, (uint)(i + 1));
        }

        return data;
    }

    private static byte[] Head()
    {
        var data = new byte[54];
        Put32(data, 0, 0x00010000);
        Put32(data, 12, 0x5F0F3CF5);
        Put16(data, 18, 1000);
        return data;
    }

    private static byte[] Hhea(int glyphs)
    {
        var data = new byte[36];
        Put32(data, 0, 0x00010000);
        Put16(data, 34, glyphs);
        return data;
    }

    private static byte[] Maxp(int glyphs)
    {
        var data = new byte[6];
        Put32(data, 0, 0x00005000);
        Put16(data, 4, glyphs);
        return data;
    }

    private static byte[] Name(string family)
    {
        var text = Encoding.BigEndianUnicode.GetBytes(family);
        var data = new byte[6 + 12 + text.Length];
        Put16(data, 2, 1);
        Put16(data, 4, 18);
        Put16(data, 6, 3);
        Put16(data, 8, 1);
        Put16(data, 10, 0x409);
        Put16(data, 12, 1);
        Put16(data, 14, text.Length);
        text.CopyTo(data, 18);
        return data;
    }

    private static byte[] Post()
    {
        var data = new byte[32];
        Put32(data, 0, 0x00030000);
        return data;
    }

    private static void WriteBase128(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[5];
        var count = 0;
        do
        {
            bytes[count++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value != 0);

        for (var i = count - 1; i >= 0; i--)
        {
            output.WriteByte((byte)(bytes[i] | (i > 0 ? 0x80 : 0)));
        }
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        Span<byte> word = stackalloc byte[4];
        for (var i = 0; i < data.Length; i += 4)
        {
            word.Clear();
            data.AsSpan(i, Math.Min(4, data.Length - i)).CopyTo(word);
            sum += BinaryPrimitives.ReadUInt32BigEndian(word);
        }

        return sum;
    }

    private static uint Tag(string tag) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(tag));

    private static void Pad(Stream output)
    {
        while (output.Length % 4 != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void Put16(Span<byte> data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data[offset..], (ushort)value);

    private static void Put32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);
}
