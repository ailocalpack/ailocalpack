using System.Buffers.Binary;
using System.Collections;
using System.IO.Compression;
using System.Text;

namespace AiLocalPack.Toolkit.Fonts;

/// <summary>The Unicode code points a font has a glyph for (its <c>cmap</c>).</summary>
public sealed class FontCharacters
{
    private const int MaxCodePoint = 0x10FFFF;

    private readonly BitArray _bits = new(MaxCodePoint + 1);

    /// <summary>Code points with a glyph.</summary>
    public int Count { get; private set; }

    public bool Contains(int codePoint) => codePoint is >= 0 and <= MaxCodePoint && _bits[codePoint];

    /// <summary>Whether every code point of <paramref name="codePoints"/> has a glyph.</summary>
    public bool ContainsAll(IEnumerable<int> codePoints) => codePoints.All(Contains);

    internal void Add(int codePoint)
    {
        if (codePoint is >= 0 and <= MaxCodePoint && !_bits[codePoint])
        {
            _bits[codePoint] = true;
            Count++;
        }
    }
}

/// <summary>What <see cref="OpenTypeFont.Read"/> found in a font file.</summary>
/// <param name="Format"><c>ttf</c>, <c>otf</c> (CFF outlines), <c>ttc</c>, <c>woff</c> or <c>woff2</c>.</param>
/// <param name="FamilyName">The family name from the <c>name</c> table (typographic family first), or <c>null</c>.</param>
public sealed record FontInfo(string Format, string? FamilyName, FontCharacters Characters);

/// <summary>A font file can't be read: not a font, a format the reader lacks, or damaged.</summary>
public sealed class FontFormatException : Exception
{
    public FontFormatException()
    {
    }

    public FontFormatException(string message)
        : base(message)
    {
    }

    public FontFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads the character coverage and family name of a font file: TrueType / OpenType (<c>.ttf</c>, <c>.otf</c>), a
/// collection (<c>.ttc</c>: its first font, the one engines load) and the web wrappers WOFF (zlib per table) and WOFF2
/// (one Brotli stream; <c>cmap</c> and <c>name</c> are never transformed). Coverage = every code point a Unicode
/// <c>cmap</c> subtable (platform 0, or Windows 3/1 and 3/10) maps to a glyph other than 0, in formats 0, 4, 6, 10,
/// 12 and 13 (14, variation sequences, adds no characters). Symbol fonts (3/0) and legacy CJK encodings have none.
/// Only reads; every offset is checked, so a damaged file gives a <see cref="FontFormatException"/>.
/// </summary>
public static class OpenTypeFont
{
    private const uint TrueTypeVersion = 0x00010000;
    private const uint Otto = 0x4F54544F; // 'OTTO'
    private const uint TrueTag = 0x74727565; // 'true'
    private const uint Ttcf = 0x74746366; // 'ttcf'
    private const uint Woff = 0x774F4646; // 'wOFF'
    private const uint Woff2 = 0x774F4632; // 'wOF2'
    private const uint CmapTag = 0x636D6170;
    private const uint NameTag = 0x6E616D65;

    /// <summary>Largest inflated font (WOFF / WOFF2) read: CJK fonts are ~20 MB.</summary>
    private const int MaxInflated = 128 * 1024 * 1024;

    /// <summary>Table tags of the WOFF2 "known tags" list in order (index 63 = an explicit tag follows).</summary>
    private static readonly string[] Woff2KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    /// <exception cref="FontFormatException">Not a font this reader knows, or damaged.</exception>
    public static FontInfo Read(ReadOnlySpan<byte> data)
    {
        try
        {
            var signature = U32(data, 0);
            switch (signature)
            {
                case TrueTypeVersion or TrueTag:
                    return FromSfnt(data, 0, "ttf");
                case Otto:
                    return FromSfnt(data, 0, "otf");
                case Ttcf:
                    if (U32(data, 8) == 0)
                    {
                        throw new FontFormatException("The font collection holds no fonts.");
                    }

                    return FromSfnt(data, checked((int)U32(data, 12)), "ttc");
                case Woff:
                    return FromWoff(data);
                case Woff2:
                    return FromWoff2(data);
                default:
                    throw new FontFormatException("Not a TrueType, OpenType, WOFF or WOFF2 font.");
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or InvalidDataException)
        {
            throw new FontFormatException($"The font is damaged: {e.Message}", e);
        }
    }

    /// <summary>Reads the file at <paramref name="path"/>.</summary>
    /// <exception cref="FontFormatException">Not a font this reader knows, or damaged.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    public static FontInfo Read(string path) => Read(File.ReadAllBytes(path));

    private static FontInfo FromSfnt(ReadOnlySpan<byte> data, int start, string format)
    {
        var flavor = U32(data, start);
        if (flavor is not (TrueTypeVersion or Otto or TrueTag))
        {
            throw new FontFormatException("The font has an unknown outline format.");
        }

        var count = U16(data, start + 4);
        ReadOnlySpan<byte> cmap = default;
        ReadOnlySpan<byte> name = default;
        for (var i = 0; i < count; i++)
        {
            var record = start + 12 + (i * 16);
            var tag = U32(data, record);
            if (tag is CmapTag or NameTag)
            {
                var table = Slice(data, checked((int)U32(data, record + 8)), checked((int)U32(data, record + 12)));
                if (tag == CmapTag)
                {
                    cmap = table;
                }
                else
                {
                    name = table;
                }
            }
        }

        return Info(format == "ttf" && flavor == Otto ? "otf" : format, cmap, name);
    }

    private static FontInfo FromWoff(ReadOnlySpan<byte> data)
    {
        var count = U16(data, 12);
        byte[]? cmap = null;
        byte[]? name = null;
        for (var i = 0; i < count; i++)
        {
            var record = 44 + (i * 20);
            var tag = U32(data, record);
            if (tag is not (CmapTag or NameTag))
            {
                continue;
            }

            var compressed = Slice(data, checked((int)U32(data, record + 4)), checked((int)U32(data, record + 8)));
            var length = checked((int)U32(data, record + 12));
            if (length > MaxInflated)
            {
                throw new FontFormatException("A table of the font is too large.");
            }

            var table = compressed.Length == length ? compressed.ToArray() : Inflate(compressed, length);
            if (tag == CmapTag)
            {
                cmap = table;
            }
            else
            {
                name = table;
            }
        }

        return Info("woff", cmap, name);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int length)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var table = new byte[length];
        zlib.ReadExactly(table);
        return table;
    }

    private static FontInfo FromWoff2(ReadOnlySpan<byte> data)
    {
        if (U32(data, 4) == Ttcf)
        {
            throw new FontFormatException("WOFF2 font collections are not supported.");
        }

        var count = U16(data, 12);
        var compressedLength = checked((int)U32(data, 20));
        var position = 48;
        var tables = new List<(string Tag, long Length)>();
        for (var i = 0; i < count; i++)
        {
            var flags = data[position++];
            var tagIndex = flags & 0x3F;
            string tag;
            if (tagIndex == 63)
            {
                tag = Encoding.ASCII.GetString(Slice(data, position, 4));
                position += 4;
            }
            else
            {
                tag = Woff2KnownTags[tagIndex];
            }

            var transform = (flags >> 6) & 0x03;
            var length = Base128(data, ref position);

            // glyf / loca are transformed unless version 3; any other table only with a version other than 0.
            var transformed = tag is "glyf" or "loca" ? transform != 3 : transform != 0;
            if (transformed)
            {
                length = Base128(data, ref position);
            }

            tables.Add((tag, length));
        }

        var total = tables.Sum(t => t.Length);
        if (total > MaxInflated)
        {
            throw new FontFormatException("The font is too large.");
        }

        var inflated = new byte[total];
        if (!BrotliDecoder.TryDecompress(Slice(data, position, compressedLength), inflated, out var written) || written != total)
        {
            throw new FontFormatException("The font's compressed data is damaged.");
        }

        ReadOnlySpan<byte> cmap = default;
        ReadOnlySpan<byte> name = default;
        var offset = 0;
        foreach (var (tag, length) in tables)
        {
            if (tag == "cmap")
            {
                cmap = inflated.AsSpan(offset, (int)length);
            }
            else if (tag == "name")
            {
                name = inflated.AsSpan(offset, (int)length);
            }

            offset += (int)length;
        }

        return Info("woff2", cmap, name);
    }

    private static long Base128(ReadOnlySpan<byte> data, ref int position)
    {
        long value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = data[position++];
            if (i == 0 && b == 0x80)
            {
                throw new FontFormatException("The font's table directory is damaged (leading zero).");
            }

            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value <= uint.MaxValue ? value : throw new FontFormatException("The font's table directory is damaged (length).");
            }
        }

        throw new FontFormatException("The font's table directory is damaged (length).");
    }

    private static FontInfo Info(string format, ReadOnlySpan<byte> cmap, ReadOnlySpan<byte> name)
    {
        if (cmap.IsEmpty)
        {
            throw new FontFormatException("The font has no character map (cmap).");
        }

        var characters = new FontCharacters();
        ReadCmap(cmap, characters);
        return new FontInfo(format, name.IsEmpty ? null : FamilyName(name), characters);
    }

    private static void ReadCmap(ReadOnlySpan<byte> cmap, FontCharacters characters)
    {
        var count = U16(cmap, 2);
        var read = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var record = 4 + (i * 8);
            var platform = U16(cmap, record);
            var encoding = U16(cmap, record + 2);
            var offset = U32(cmap, record + 4);
            var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);

            // Several records may share one subtable.
            if (unicode && read.Add(offset))
            {
                ReadSubtable(cmap, checked((int)offset), characters);
            }
        }
    }

    private static void ReadSubtable(ReadOnlySpan<byte> cmap, int start, FontCharacters characters)
    {
        switch (U16(cmap, start))
        {
            case 0:
                for (var c = 0; c < 256; c++)
                {
                    if (U8(cmap, start + 6 + c) != 0)
                    {
                        characters.Add(c);
                    }
                }

                break;
            case 4:
                ReadFormat4(cmap, start, characters);
                break;
            case 6:
            {
                var first = U16(cmap, start + 6);
                var entries = U16(cmap, start + 8);
                for (var i = 0; i < entries; i++)
                {
                    if (U16(cmap, start + 10 + (i * 2)) != 0)
                    {
                        characters.Add(first + i);
                    }
                }

                break;
            }

            case 10:
            {
                var first = checked((int)U32(cmap, start + 12));
                var entries = checked((int)U32(cmap, start + 16));
                for (var i = 0; i < entries; i++)
                {
                    if (U16(cmap, start + 20 + (i * 2)) != 0)
                    {
                        characters.Add(first + i);
                    }
                }

                break;
            }

            case 12 or 13:
            {
                var manyToOne = U16(cmap, start) == 13;
                var groups = checked((int)U32(cmap, start + 12));
                for (var g = 0; g < groups; g++)
                {
                    var group = start + 16 + (g * 12);
                    var first = U32(cmap, group);
                    var last = Math.Min(U32(cmap, group + 4), 0x10FFFFu);
                    var glyph = U32(cmap, group + 8);
                    for (var c = first; c <= last && first <= last; c++)
                    {
                        var mapped = manyToOne ? glyph : glyph + (c - first);
                        if (mapped != 0)
                        {
                            characters.Add((int)c);
                        }
                    }
                }

                break;
            }

            default:
                // 2 (legacy CJK encodings) is never a Unicode subtable; 14 (variation sequences) adds no characters.
                break;
        }
    }

    private static void ReadFormat4(ReadOnlySpan<byte> cmap, int start, FontCharacters characters)
    {
        var segments = U16(cmap, start + 6) / 2;
        var ends = start + 14;
        var starts = ends + (segments * 2) + 2;
        var deltas = starts + (segments * 2);
        var rangeOffsets = deltas + (segments * 2);
        for (var s = 0; s < segments; s++)
        {
            var end = U16(cmap, ends + (s * 2));
            var first = U16(cmap, starts + (s * 2));
            var delta = U16(cmap, deltas + (s * 2));
            var rangeOffsetAt = rangeOffsets + (s * 2);
            var rangeOffset = U16(cmap, rangeOffsetAt);
            for (var c = first; c <= end && first <= end; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    // idRangeOffset counts bytes from its own position into glyphIdArray.
                    var at = rangeOffsetAt + rangeOffset + ((c - first) * 2);
                    var id = U16(cmap, at);
                    glyph = id == 0 ? 0 : (id + delta) & 0xFFFF;
                }

                if (glyph != 0 && c != 0xFFFF)
                {
                    characters.Add(c);
                }
            }
        }
    }

    /// <summary>Typographic family (name ID 16) or family (1): Windows English first, then any Windows, Unicode, Mac Roman.</summary>
    private static string? FamilyName(ReadOnlySpan<byte> name)
    {
        try
        {
            var count = U16(name, 2);
            var storage = U16(name, 4);
            string? best = null;
            var bestRank = int.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var record = 6 + (i * 12);
                var platform = U16(name, record);
                var encoding = U16(name, record + 2);
                var language = U16(name, record + 4);
                var id = U16(name, record + 6);
                if (id is not (1 or 16))
                {
                    continue;
                }

                var platformRank = platform switch
                {
                    3 when language == 0x409 => 0,
                    3 => 1,
                    0 => 2,
                    1 when encoding == 0 => 3,
                    _ => -1,
                };
                if (platformRank < 0)
                {
                    continue;
                }

                var rank = (id == 16 ? 0 : 10) + platformRank;
                if (rank >= bestRank)
                {
                    continue;
                }

                var bytes = Slice(name, storage + U16(name, record + 10), U16(name, record + 8));
                var text = platform == 1 ? Encoding.Latin1.GetString(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
                if (text.Trim().Length > 0)
                {
                    best = text.Trim();
                    bestRank = rank;
                }
            }

            return best;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A damaged name table only loses the name.
            return null;
        }
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new FontFormatException($"A table of the font lies outside the file (offset {offset}, length {length}).");
        }

        return data.Slice(offset, length);
    }

    private static int U8(ReadOnlySpan<byte> data, int offset) => Slice(data, offset, 1)[0];

    private static int U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(Slice(data, offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(Slice(data, offset, 4));
}
