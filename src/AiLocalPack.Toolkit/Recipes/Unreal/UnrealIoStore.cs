using System.Buffers.Binary;
using System.Text;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>
/// Read-only, indexed non-asset files. TOC versions describe container layout; no cooked package version is inferred.
/// Global script/package metadata is unnecessary for locres. This is not a runtime IoStore mod writer.
/// </summary>
internal sealed class UnrealIoStore : IDisposable
{
    private readonly IoStoreReader _reader;
    private readonly FByteArchive _tocArchive;
    public IReadOnlyDictionary<string, GameFile> Files { get; }
    public IReadOnlyList<string> Containers { get; }

    /// <summary>The TOC version (3–8).</summary>
    public byte Version { get; }

    public UnrealIoStore(string path, CancellationToken ct, Func<string, FArchive>? openPartition = null)
    {
        ct.ThrowIfCancellationRequested();
        byte[] snapshot;
        using (var file = File.OpenRead(path))
        {
            var length = file.Length;
            if (length is < 144 or > 128L << 20)
            {
                throw new InvalidDataException("Invalid IoStore TOC size (maximum 128 MiB).");
            }

            snapshot = new byte[(int)length];
            file.ReadExactly(snapshot);
        }

        ct.ThrowIfCancellationRequested();
        // Validate the same bytes CUE4Parse receives. Reopening the path would permit an update to replace a checked TOC.
        using var input = new MemoryStream(snapshot, false);
        var header = snapshot;
        if (!header.AsSpan(0, 16).SequenceEqual("-==--==--==--==-"u8))
        {
            throw new InvalidDataException("Invalid IoStore TOC magic.");
        }

        var version = header[16];
        Version = version;
        if (version is < 3 or > 8)
        {
            throw new InvalidDataException($"Unsupported IoStore TOC version {version}; indexed versions 3–8 are readable.");
        }

        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
        var flags = header[80];
        if ((flags & 2) != 0)
        {
            throw new RecipeException("AES-encrypted IoStore archive with no known key", path);
        }

        if ((flags & (4 | 16)) != 0)
        {
            throw new InvalidDataException("Signed or on-demand IoStore containers are unsupported.");
        }

        if ((flags & 8) == 0 || U32(48) == 0)
        {
            throw new InvalidDataException("IoStore localization requires a directory index; cooked FText is unsupported.");
        }

        if (U32(20) != 144 || U32(32) != 12 || U32(40) != 32 || U32(36) > 16 ||
            U32(24) > 1_000_000 || U32(28) > 4_000_000 || U32(84) > 1_000_000 || U32(96) > 1_000_000 ||
            U32(44) is 0 or > 16 << 20 || (U32(44) & (U32(44) - 1)) != 0 || U32(52) is 0 or > 1024)
        {
            throw new InvalidDataException("Invalid or oversized IoStore TOC header.");
        }

        var directoryOffset = 144L + U32(24) * 22L + U32(28) * 12L + U32(36) * 32L +
            (version >= 4 ? U32(84) * 4L : 0) + (version >= 5 ? U32(96) * 4L : 0);
        var metadataSize = U32(24) * (version < 8 ? 33L : 24L); // v8 IO hash + flags + three padding bytes
        if (directoryOffset + U32(48) + metadataSize > input.Length)
        {
            throw new InvalidDataException("Truncated IoStore TOC.");
        }

        var partitionSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(88));
        if (partitionSize == 0)
        {
            throw new InvalidDataException("Invalid IoStore partition size.");
        }

        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        Containers = new[] { path }.Concat(Enumerable.Range(0, (int)U32(52)).Select(i => stem + (i == 0 ? "" : $"_s{i}") + ".ucas")).ToList();
        foreach (var partition in Containers.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(partition))
            {
                throw new RecipeException("Missing IoStore data partition.", partition);
            }
        }

        // Read directory ourselves: bounded arrays, safe paths and cycle detection before exposing anything to CUE4Parse.
        input.Position = directoryOffset;
        var directory = new byte[U32(48)];
        input.ReadExactly(directory);
        var indexed = ReadDirectory(directory, U32(24), ct);
        var versions = new VersionContainer(EGame.GAME_UE5_0);
        _tocArchive = new FByteArchive(path, snapshot, versions);
        var opened = new List<FArchive>();
        try
        {
            // The upstream constructor doesn't close previously opened partitions if a later open fails.
            _reader = new IoStoreReader(_tocArchive, partition =>
            {
                ct.ThrowIfCancellationRequested();
                var archive = openPartition is null
                    ? new FStreamArchive(partition, File.Open(partition, FileMode.Open, FileAccess.Read, FileShare.Read), versions)
                    : openPartition(partition);
                opened.Add(archive);
                return archive;
            });
            var toc = _reader.TocResource;
            foreach (var block in toc.CompressionBlocks)
            {
                ct.ThrowIfCancellationRequested();
                var partition = (ulong)block.Offset / partitionSize;
                var offset = (ulong)block.Offset % partitionSize;
                if (partition >= (ulong)_reader.ContainerStreams.Count || block.CompressionMethodIndex >= toc.CompressionMethods.Length ||
                    block.UncompressedSize is 0 || block.UncompressedSize > U32(44) || block.CompressedSize is 0 ||
                    offset + ((block.CompressedSize + 15UL) & ~15UL) > (ulong)_reader.ContainerStreams[(int)partition].Length ||
                    (block.CompressionMethodIndex == 0 && block.CompressedSize != block.UncompressedSize))
                {
                    throw new InvalidDataException("Invalid IoStore compression block or truncated data partition.");
                }
            }

            var files = new Dictionary<string, GameFile>(StringComparer.Ordinal);
            foreach (var (name, index) in indexed)
            {
                ct.ThrowIfCancellationRequested();
                var extent = toc.ChunkOffsetLengths[index];
                if (extent.Length > 0 && (extent.Offset + extent.Length - 1) / U32(44) >= (ulong)toc.CompressionBlocks.Length)
                {
                    throw new InvalidDataException("IoStore chunk lies outside compression blocks.");
                }

                if (extent.Length > 0)
                {
                    var end = extent.Offset + extent.Length - 1;
                    if (end % U32(44) >= toc.CompressionBlocks[end / U32(44)].UncompressedSize)
                    {
                        throw new InvalidDataException("IoStore chunk lies outside its data block.");
                    }
                }

                files.Add(name, new FIoStoreEntry(_reader, name, index));
            }

            Files = files;
        }
        catch
        {
            foreach (var archive in opened)
            {
                archive.Dispose();
            }

            _tocArchive.Dispose();
            // IoStoreReader wraps exceptions from the stream factory, including cancellation.
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static List<(string Path, uint Index)> ReadDirectory(byte[] bytes, uint chunks, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var mount = UnrealLocres.ReadString(reader);
        if (!mount.StartsWith("../../../", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported IoStore mount point.");
        }

        mount = mount[9..].TrimEnd('/');
        if (mount.Length > 4096 || mount.Any(c => c is '\\' or ':' or '\0' || char.IsControl(c)) ||
            (mount.Length > 0 && mount.Split('/').Any(p => p is "" or "." or "..")))
        {
            throw new InvalidDataException("Invalid IoStore mount point.");
        }

        var directories = new (uint Name, uint Child, uint Sibling, uint File)[UnrealLocres.Count(reader)];
        for (var i = 0; i < directories.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            directories[i] = (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        }

        var files = new (uint Name, uint Next, uint Chunk)[UnrealLocres.Count(reader)];
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            files[i] = (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        }

        var names = new string[UnrealLocres.Count(reader)];
        for (var i = 0; i < names.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            names[i] = UnrealLocres.ReadString(reader);
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Unexpected IoStore directory data.");
        }

        var pending = new Stack<(uint Directory, string Parent)>();
        pending.Push((0, mount));
        var visited = new HashSet<uint>();
        var seenFiles = new HashSet<uint>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, uint)>();
        string Name(uint index) => index < names.Length && names[index].Length > 0 &&
            !names[index].Any(c => c is '/' or '\\' or ':' or '\0' || char.IsControl(c)) && names[index] is not ("." or "..")
            ? names[index] : throw new InvalidDataException("Invalid IoStore directory name.");
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Directory >= directories.Length || !visited.Add(item.Directory))
            {
                throw new InvalidDataException("Invalid or cyclic IoStore directory index.");
            }

            var dir = directories[item.Directory];
            var path = dir.Name == uint.MaxValue ? item.Parent : item.Parent + "/" + Name(dir.Name);
            path = path.TrimStart('/');
            if (path.Length > 4096)
            {
                throw new InvalidDataException("IoStore path is too long.");
            }

            if (dir.Sibling != uint.MaxValue)
            {
                pending.Push((dir.Sibling, item.Parent));
            }

            if (dir.Child != uint.MaxValue)
            {
                pending.Push((dir.Child, path));
            }

            var fileIndex = dir.File;
            while (fileIndex != uint.MaxValue)
            {
                ct.ThrowIfCancellationRequested();
                if (fileIndex >= files.Length || !seenFiles.Add(fileIndex))
                {
                    throw new InvalidDataException("Invalid or cyclic IoStore file index.");
                }

                var file = files[fileIndex];
                var full = path.Length == 0 ? Name(file.Name) : path + "/" + Name(file.Name);
                if (full.Length > 4096 || file.Chunk >= chunks || !paths.Add(full) || full.Split('/').Any(p => p is "" or "." or ".."))
                {
                    throw new InvalidDataException("Invalid or duplicate IoStore file path/chunk.");
                }

                result.Add((full, file.Chunk));
                fileIndex = file.Next;
            }
        }

        if (visited.Count != directories.Length || seenFiles.Count != files.Length)
        {
            throw new InvalidDataException("Unreachable IoStore directory entries.");
        }

        return result;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _tocArchive.Dispose();
    }
}
