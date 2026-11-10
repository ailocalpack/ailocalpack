using System.IO.Compression;
using System.Security.Cryptography;

namespace AiLocalPack.Core.Patches;

/// <summary>A patch archive written by <see cref="PatchArchiveWriter"/>.</summary>
/// <param name="Sha256">SHA-256 of the archive (the manifest's <c>patch.sha256</c>).</param>
/// <param name="Size">Its size in bytes (<c>patch.size</c>).</param>
/// <param name="Originals">Each file → SHA-256 of the original it patches (the manifest build's <c>files</c>).</param>
/// <param name="Patched">Each file → SHA-256 after patching (<c>patch.files</c>).</param>
public sealed record PatchArchive(string Sha256, long Size, IReadOnlyDictionary<string, string> Originals, IReadOnlyDictionary<string, string> Patched);

/// <summary>
/// Writes a binary patch pack's archive (task 57): one ZIP entry <c>&lt;path&gt;.vcdiff</c> per file, deflated, in
/// ordinal order with a fixed time stamp (the same files give the same archive), each delta made by
/// <see cref="VcdiffEncoder"/> from the original to the new file; then every entry is decoded again
/// (<see cref="VcdiffDecoder"/>) and checked against the new file's hash, so a pack never ships a delta that doesn't
/// apply. The archive is written next to its place and renamed in.
/// </summary>
public static class PatchArchiveWriter
{
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="archivePath">Where the archive goes (replaced).</param>
    /// <param name="originalRoot">Folder holding the original files (the working copy).</param>
    /// <param name="patchedRoot">Folder holding the new files at the same relative paths (the job's build folder).</param>
    /// <param name="files">Relative, <c>/</c>-separated paths present in both.</param>
    /// <exception cref="IOException">A file can't be read or written, or a delta doesn't decode to the new file.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be read or written.</exception>
    public static PatchArchive Write(string archivePath, string originalRoot, string patchedRoot, IEnumerable<string> files, VcdiffEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentNullException.ThrowIfNull(files);
        var ordered = files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var originals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var patched = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var folder = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in ordered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var original = Local(originalRoot, file);
                        var target = Local(patchedRoot, file);
                        originals[file] = HashFile(original);
                        patched[file] = HashFile(target);
                        using var delta = new MemoryStream();
                        VcdiffEncoder.EncodeFile(original, target, delta, options, cancellationToken);
                        Check(file, original, delta, patched[file]);
                        var entry = zip.CreateEntry(file + ".vcdiff", CompressionLevel.Optimal);
                        entry.LastWriteTime = EntryTime;
                        using var entryStream = entry.Open();
                        delta.Position = 0;
                        delta.CopyTo(entryStream);
                    }
                }

                stream.Flush(flushToDisk: true);
            }

            var bytes = new FileInfo(temp).Length;
            string sha;
            using (var read = File.OpenRead(temp))
            {
                sha = Convert.ToHexStringLower(SHA256.HashData(read));
            }

            File.Move(temp, archivePath, overwrite: true);
            return new PatchArchive(sha, bytes, originals, patched);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Decodes the delta again and compares the result with the new file's hash.</summary>
    private static void Check(string file, string original, MemoryStream delta, string expected)
    {
        delta.Position = 0;
        using var source = File.OpenRead(original);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var sink = new HashingStream(hash))
        {
            try
            {
                VcdiffDecoder.Decode(source, delta, sink);
            }
            catch (VcdiffException e)
            {
                throw new IOException($"The patch of {file} doesn't apply to its original: {e.Message}", e);
            }
        }

        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
        {
            throw new IOException($"The patch of {file} doesn't give the new file back.");
        }
    }

    private static string Local(string root, string file)
    {
        if (!Packs.PackFormat.IsRelativePath(file))
        {
            throw new IOException($"'{file}' is not a relative path inside the game folder.");
        }

        return Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A write-only stream that only hashes what is written.</summary>
    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
    }
}
