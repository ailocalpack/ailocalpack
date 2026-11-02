using System.Buffers.Binary;
using System.Security;

namespace AiLocalPack.Toolkit.Engines;

/// <summary>Small reads from game files. Unreadable files give "not found", never an exception.</summary>
internal static class BinaryProbe
{
    private const int ChunkSize = 1 << 20;

    /// <summary><paramref name="count"/> bytes at <paramref name="offset"/>, or <c>null</c> if the file is shorter or unreadable.</summary>
    public static byte[]? ReadAt(string path, long offset, int count)
    {
        try
        {
            using var stream = Open(path);
            if (offset < 0 || offset > stream.Length - count)
            {
                return null;
            }

            stream.Position = offset;
            var buffer = new byte[count];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception e) when (IsDiskError(e))
        {
            return null;
        }
    }

    public static long? Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (IsDiskError(e))
        {
            return null;
        }
    }

    /// <summary>ELF binary (Linux executable without an extension).</summary>
    public static bool IsElf(string path) => ReadAt(path, 0, 4) is [0x7F, (byte)'E', (byte)'L', (byte)'F'];

    public static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    public static ulong ReadUInt64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));

    /// <summary>
    /// Streams the file and returns the index of the first of <paramref name="patterns"/> found (earlier patterns win
    /// inside one chunk), or -1 when none is found or the file is unreadable.
    /// </summary>
    public static int FindFirst(string path, IReadOnlyList<byte[]> patterns)
    {
        var overlap = patterns.Max(p => p.Length) - 1;
        var buffer = new byte[ChunkSize + overlap];
        try
        {
            using var stream = Open(path);
            var kept = 0;
            while (true)
            {
                var read = stream.Read(buffer, kept, buffer.Length - kept);
                if (read == 0)
                {
                    return -1;
                }

                var span = buffer.AsSpan(0, kept + read);
                for (var i = 0; i < patterns.Count; i++)
                {
                    if (span.IndexOf(patterns[i]) >= 0)
                    {
                        return i;
                    }
                }

                kept = Math.Min(overlap, span.Length);
                span[^kept..].CopyTo(buffer);
            }
        }
        catch (Exception e) when (IsDiskError(e))
        {
            return -1;
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

    private static bool IsDiskError(Exception e) => e is IOException or UnauthorizedAccessException or SecurityException;
}
