using System.Security.Cryptography;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>
/// Uncompressed, unsigned pak v3 (UE4.3+). Full directory index, SHA-1 of payloads and index, relative paths under
/// ../../../. Deliberately writes no executable content and requires no Unreal tools or native writer.
/// Layout reference: CUE4Parse UE4/Pak/Objects/FPakEntry.cs and FPakInfo.cs (Apache-2.0).
/// </summary>
internal static class UnrealPakWriter
{
    public static byte[] Write(IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        var entries = new List<(string Path, long Offset, byte[] Bytes, byte[] Hash)>();
        foreach (var (path, bytes) in files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)
                || path.Split('/').Any(p => p is "" or "." or ".."))
            {
                throw new InvalidDataException("Unsafe pak entry path.");
            }

            var offset = stream.Position;
            var hash = SHA1.HashData(bytes);
            Entry(writer, 0, bytes.Length, hash);
            writer.Write(bytes);
            entries.Add((path, offset, bytes, hash));
        }

        var indexOffset = stream.Position;
        using var index = new MemoryStream();
        using (var iw = new BinaryWriter(index, Encoding.UTF8, true))
        {
            UnrealLocres.WriteString(iw, "../../../");
            iw.Write(entries.Count);
            foreach (var entry in entries)
            {
                UnrealLocres.WriteString(iw, entry.Path);
                Entry(iw, entry.Offset, entry.Bytes.Length, entry.Hash);
            }
        }

        var indexBytes = index.ToArray();
        writer.Write(indexBytes);
        writer.Write(0x5A6F12E1u);
        writer.Write(3);
        writer.Write(indexOffset);
        writer.Write((long)indexBytes.Length);
        writer.Write(SHA1.HashData(indexBytes));
        return stream.ToArray();
    }

    private static void Entry(BinaryWriter writer, long offset, long size, byte[] hash)
    {
        writer.Write(offset);
        writer.Write(size);
        writer.Write(size);
        writer.Write(0); // Compression method: none; no compression-block array follows.
        writer.Write(hash);
        writer.Write((byte)0); // Not encrypted, not deleted.
        writer.Write(0); // Compression block size.
    }
}
