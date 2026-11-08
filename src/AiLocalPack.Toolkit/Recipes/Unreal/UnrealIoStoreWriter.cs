using System.Buffers.Binary;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>
/// The empty IoStore container (<c>.utoc</c> without chunks + empty <c>.ucas</c>) that has to sit next to a mod pak in a
/// UE5 IoStore game: such a game mounts a <c>.pak</c> only together with its <c>.utoc</c>/<c>.ucas</c> (checked on
/// Golfing in Aether, UE 5.3+, 2026-11-08: the pak alone is ignored, with the empty pair its locres are used).
/// Layout reference: CUE4Parse <c>FIoStoreTocHeader</c> (Apache-2.0).
/// </summary>
internal static class UnrealIoStoreWriter
{
    private const int HeaderSize = 144;

    /// <param name="version">The game's own TOC version, so an older UE5 doesn't refuse a newer format.</param>
    /// <param name="name">The container's name, hashed into its ID.</param>
    public static byte[] EmptyToc(byte version, string name)
    {
        var header = new byte[HeaderSize];
        "-==--==--==--==-"u8.CopyTo(header);
        header[16] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), 12); // compressed block entry size
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), 32); // compression method name length
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 64 * 1024); // compression block size
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), 1); // partitions
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), BinaryPrimitives.ReadUInt64LittleEndian(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(88), ulong.MaxValue); // partition size: unlimited
        return header;
    }
}
