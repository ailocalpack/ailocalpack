using System.Buffers.Binary;
using System.Text;

namespace AiLocalPack.TestKit;

/// <summary>
/// Synthetic icon containers for icon-extraction tests: a PNG header, a Windows PE file with icon resources, a macOS
/// <c>.icns</c> file. Only the structure readers look at, never real images.
/// </summary>
public static class IconFixture
{
    /// <summary>The start of a PNG of the given width: signature + IHDR chunk, then <paramref name="tag"/> to tell images apart.</summary>
    public static byte[] Png(int width, string tag = "")
    {
        var bytes = new byte[33 + tag.Length];
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)width);
        bytes[24] = 8;
        bytes[25] = 6;
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 33);
        return bytes;
    }

    /// <summary>A bitmap (DIB) icon image: a <c>BITMAPINFOHEADER</c> start and filler.</summary>
    public static byte[] Dib(int width, string tag = "")
    {
        var bytes = new byte[40 + tag.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), width * 2);
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 40);
        return bytes;
    }

    /// <summary>One image of an icon group: its size as stored in the group (256 is stored as 0), bit depth and data.</summary>
    public sealed record IconImage(int Width, ushort BitCount, byte[] Data);

    /// <summary>
    /// A minimal PE32+ (or PE32) file with one <c>.rsrc</c> section holding an <c>RT_GROUP_ICON</c> (ID 1) over the given
    /// <c>RT_ICON</c> images (IDs 1…n). With no images there is no resource directory at all.
    /// </summary>
    public static byte[] Pe(IReadOnlyList<IconImage> icons, bool pe32Plus = true)
    {
        const int SectionRva = 0x1000;
        const int SectionOffset = 0x200;
        var optionalSize = pe32Plus ? 240 : 224;
        var file = new MemoryStream();
        var w = new BinaryWriter(file);

        // DOS header with e_lfanew = 0x40.
        w.Write((ushort)0x5A4D);
        file.Position = 0x3C;
        w.Write(0x40);
        file.Position = 0x40;
        w.Write(0x00004550);

        // COFF header.
        w.Write((ushort)(pe32Plus ? 0x8664 : 0x14C));
        w.Write((ushort)1);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write((ushort)optionalSize);
        w.Write((ushort)0x22);

        // Optional header: magic, then zeros up to the data directories.
        var optional = file.Position;
        w.Write((ushort)(pe32Plus ? 0x20B : 0x10B));
        var directories = optional + (pe32Plus ? 112 : 96);
        file.Position = directories - 4;
        w.Write(16);
        var rsrc = BuildResources(icons, SectionRva);
        if (icons.Count > 0)
        {
            file.Position = directories + 16;
            w.Write(SectionRva);
            w.Write(rsrc.Length);
        }

        // Section table: one .rsrc section.
        file.Position = optional + optionalSize;
        w.Write(".rsrc\0\0\0"u8.ToArray());
        w.Write(rsrc.Length);
        w.Write(SectionRva);
        w.Write(rsrc.Length);
        w.Write(SectionOffset);
        file.Position = SectionOffset;
        w.Write(rsrc);
        return file.ToArray();
    }

    /// <summary>Resource tree: root → {RT_ICON → ids 1..n, RT_GROUP_ICON → id 1} → language 1033 → data.</summary>
    private static byte[] BuildResources(IReadOnlyList<IconImage> icons, int sectionRva)
    {
        if (icons.Count == 0)
        {
            return new byte[16];
        }

        var group = new MemoryStream();
        var g = new BinaryWriter(group);
        g.Write((ushort)0);
        g.Write((ushort)1);
        g.Write((ushort)icons.Count);
        for (var i = 0; i < icons.Count; i++)
        {
            var icon = icons[i];
            g.Write((byte)(icon.Width >= 256 ? 0 : icon.Width));
            g.Write((byte)(icon.Width >= 256 ? 0 : icon.Width));
            g.Write((byte)0);
            g.Write((byte)0);
            g.Write((ushort)1);
            g.Write(icon.BitCount);
            g.Write(icon.Data.Length);
            g.Write((ushort)(i + 1));
        }

        // Layout: directories first (fixed sizes), then data entries, then data blobs.
        var leaves = icons.Select(i => i.Data).Append(group.ToArray()).ToList();
        int Dir(int entries) => 16 + (entries * 8);
        var rootAt = 0;
        var iconTypeAt = rootAt + Dir(2);
        var groupTypeAt = iconTypeAt + Dir(icons.Count);
        var languagesAt = groupTypeAt + Dir(1);
        var dataEntriesAt = languagesAt + (leaves.Count * Dir(1));
        var blobsAt = dataEntriesAt + (leaves.Count * 16);
        var total = blobsAt + leaves.Sum(l => l.Length + 8);
        var buffer = new byte[total];

        void Directory(int at, int ids, params (uint Name, uint Target)[] entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 14), (ushort)ids);
            for (var i = 0; i < entries.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 16 + (i * 8)), entries[i].Name);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 20 + (i * 8)), entries[i].Target);
            }
        }

        const uint Subdirectory = 0x80000000;
        Directory(rootAt, 2, (3, Subdirectory | (uint)iconTypeAt), (14, Subdirectory | (uint)groupTypeAt));
        Directory(iconTypeAt, icons.Count, [.. icons.Select((_, i) => ((uint)(i + 1), Subdirectory | (uint)(languagesAt + (i * Dir(1)))))]);
        Directory(groupTypeAt, 1, (1, Subdirectory | (uint)(languagesAt + (icons.Count * Dir(1)))));
        var blob = blobsAt;
        for (var i = 0; i < leaves.Count; i++)
        {
            var dataEntry = dataEntriesAt + (i * 16);
            Directory(languagesAt + (i * Dir(1)), 1, (1033, (uint)dataEntry));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(dataEntry), (uint)(sectionRva + blob));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(dataEntry + 4), (uint)leaves[i].Length);
            leaves[i].CopyTo(buffer, blob);
            blob += leaves[i].Length + 8;
        }

        return buffer;
    }

    /// <summary>An <c>.icns</c> file with the given entries (4-character type, data).</summary>
    public static byte[] Icns(params (string Type, byte[] Data)[] entries)
    {
        var total = 8 + entries.Sum(e => e.Data.Length + 8);
        var bytes = new byte[total];
        "icns"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)total);
        var at = 8;
        foreach (var (type, data) in entries)
        {
            Encoding.ASCII.GetBytes(type).CopyTo(bytes, at);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 4), (uint)(data.Length + 8));
            data.CopyTo(bytes, at + 8);
            at += data.Length + 8;
        }

        return bytes;
    }

    /// <summary>An XML <c>Info.plist</c> naming the bundle icon.</summary>
    public static string Plist(string iconFile) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
            <key>CFBundleExecutable</key><string>Game</string>
            <key>CFBundleIconFile</key>
            <string>{iconFile}</string>
        </dict></plist>
        """;
}
