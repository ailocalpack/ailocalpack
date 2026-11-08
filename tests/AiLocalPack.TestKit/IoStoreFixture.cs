using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AiLocalPack.TestKit;

/// <summary>Own synthetic indexed external-file chunks, not cooked assets or a runnable UE game.</summary>
public static class IoStoreFixture
{
    public const int BlockSize = 65536;

    public static void Write(string tocPath, IReadOnlyDictionary<string, byte[]> files, byte version = 3,
        bool partitioned = false, string compression = "None")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tocPath)!);
        var entries = files.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var directory = DirectoryIndex(entries.Select(p => p.Key).ToArray());
        var blocks = entries.Select(p => Compress(p.Value, compression)).ToArray();
        if (entries.Any(p => p.Value.Length is 0 or > BlockSize)) throw new ArgumentException("Fixture expects one nonempty block per file.");
        using var toc = new MemoryStream();
        using var writer = new BinaryWriter(toc, Encoding.UTF8, true);
        var header = new byte[144];
        "-==--==--==--==-"u8.CopyTo(header);
        header[16] = version;
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);
        U32(20, 144); U32(24, (uint)entries.Length); U32(28, (uint)entries.Length); U32(32, 12);
        U32(36, compression == "None" ? 0U : 1U); U32(40, 32); U32(44, BlockSize);
        U32(48, (uint)directory.Length); U32(52, partitioned ? (uint)entries.Length : 1U);
        header[80] = compression == "None" ? (byte)8 : (byte)9;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(88), partitioned ? BlockSize : ulong.MaxValue);
        writer.Write(header);
        for (var i = 0; i < entries.Length; i++)
        {
            writer.Write((ulong)(i + 1)); writer.Write((ushort)0); writer.Write((byte)0); writer.Write((byte)7);
        }
        for (var i = 0; i < entries.Length; i++)
        {
            BigEndian40(writer, (long)i * BlockSize); BigEndian40(writer, entries[i].Value.Length);
        }
        long physical = 0;
        for (var i = 0; i < blocks.Length; i++)
        {
            writer.Write((ulong)physical | ((ulong)blocks[i].Length << 40));
            writer.Write((uint)entries[i].Value.Length | (compression == "None" ? 0U : 1U << 24));
            physical = partitioned ? (long)(i + 1) * BlockSize : physical + ((blocks[i].Length + 15) & ~15);
        }
        if (compression != "None")
        {
            var name = new byte[32];
            Encoding.ASCII.GetBytes(compression).CopyTo(name, 0);
            writer.Write(name);
        }
        writer.Write(directory);
        // v8 replaced the 32-byte chunk hash with a 20-byte IO hash. Flags follow each hash.
        foreach (var entry in entries)
        {
            var hash = System.Security.Cryptography.SHA1.HashData(entry.Value);
            writer.Write(hash); if (version < 8) writer.Write(new byte[12]);
            writer.Write(compression == "None" ? (byte)0 : (byte)1);
            if (version >= 8) writer.Write(new byte[3]);
        }
        File.WriteAllBytes(tocPath, toc.ToArray());
        var stem = Path.Combine(Path.GetDirectoryName(tocPath)!, Path.GetFileNameWithoutExtension(tocPath));
        if (partitioned)
        {
            for (var i = 0; i < blocks.Length; i++) File.WriteAllBytes(stem + (i == 0 ? "" : $"_s{i}") + ".ucas", Pad(blocks[i]));
        }
        else File.WriteAllBytes(stem + ".ucas", blocks.SelectMany(Pad).ToArray());
    }

    private static byte[] Pad(byte[] bytes)
    {
        var padded = new byte[(bytes.Length + 15) & ~15];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] Compress(byte[] bytes, string method)
    {
        if (method is not ("Zlib" or "Gzip")) return bytes;
        using var stream = new MemoryStream();
        using (Stream compressor = method == "Zlib" ? new ZLibStream(stream, CompressionLevel.SmallestSize, true) : new GZipStream(stream, CompressionLevel.SmallestSize, true))
            compressor.Write(bytes);
        return stream.ToArray();
    }

    private static void BigEndian40(BinaryWriter writer, long value)
    {
        for (var shift = 32; shift >= 0; shift -= 8) writer.Write((byte)(value >> shift));
    }

    private sealed class Dir(string name)
    {
        public string Name { get; } = name;
        public List<Dir> Children { get; } = [];
        public List<int> Files { get; } = [];
    }

    private static byte[] DirectoryIndex(string[] paths)
    {
        var root = new Dir("");
        for (var i = 0; i < paths.Length; i++)
        {
            var dir = root;
            foreach (var part in paths[i].Split('/')[..^1])
            {
                var child = dir.Children.FirstOrDefault(d => d.Name == part);
                if (child is null) { child = new Dir(part); dir.Children.Add(child); }
                dir = child;
            }
            dir.Files.Add(i);
        }
        var dirs = new List<Dir>();
        void Add(Dir dir) { dirs.Add(dir); foreach (var child in dir.Children) Add(child); }
        Add(root);
        var names = dirs.Skip(1).Select(d => d.Name).Concat(paths.Select(p => p.Split('/')[^1])).Distinct(StringComparer.Ordinal).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        FString(writer, "../../../");
        writer.Write(dirs.Count);
        foreach (var dir in dirs)
        {
            writer.Write(dir == root ? -1 : names.IndexOf(dir.Name));
            writer.Write(dir.Children.Count == 0 ? -1 : dirs.IndexOf(dir.Children[0]));
            var parent = dirs.FirstOrDefault(d => d.Children.Contains(dir));
            var sibling = parent?.Children.IndexOf(dir) + 1;
            writer.Write(parent is not null && sibling < parent.Children.Count ? dirs.IndexOf(parent.Children[sibling.Value]) : -1);
            writer.Write(dir.Files.Count == 0 ? -1 : dir.Files[0]);
        }
        writer.Write(paths.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            var peers = dirs.Single(d => d.Files.Contains(i)).Files;
            var next = peers.IndexOf(i) + 1;
            writer.Write(names.IndexOf(paths[i].Split('/')[^1])); writer.Write(next < peers.Count ? peers[next] : -1); writer.Write(i);
        }
        writer.Write(names.Count);
        foreach (var name in names) FString(writer, name);
        return stream.ToArray();
    }

    private static void FString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length + 1); writer.Write(bytes); writer.Write((byte)0);
    }
}
