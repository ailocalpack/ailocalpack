using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Godot;

/// <summary>
/// A Godot resource pack (<c>.pck</c>), standalone or embedded at the end of the executable (pack, then its size as a
/// u64 and <c>GDPC</c>). Formats: 1 (Godot 3: directory after the header, offsets from the start of the file), 2 (Godot
/// 4.0–4.4: flags and a file base; offsets from it, the base itself from the start of the file or, with
/// <c>PACK_REL_FILEBASE</c>, from the pack's start) and 3 (Godot 4.5+: directory after the files, both bases from the
/// pack's start). An encrypted directory or a sparse bundle is refused (<see cref="GodotProblem.Encrypted"/>,
/// <see cref="GodotProblem.Unsupported"/>); an encrypted file is listed (<see cref="GodotPackEntry.Encrypted"/>) and
/// copied as is. Holds the file open (read, shared) until disposed.
/// </summary>
public sealed class GodotPack : IDisposable
{
    public const uint Magic = 0x43504447; // "GDPC"

    private const uint DirectoryEncrypted = 1;
    private const uint RelativeFileBase = 2;
    private const uint SparseBundle = 4;
    private const int Alignment = 16;
    private const int MaxEntries = 1_000_000;
    private const int MaxPathLength = 4096;

    private readonly FileStream _stream;

    private GodotPack(FileStream stream, long start, bool embedded, uint format, uint major, uint minor, uint patch, uint flags, long end, IReadOnlyList<GodotPackEntry> entries)
    {
        _stream = stream;
        Start = start;
        Embedded = embedded;
        Format = format;
        Major = major;
        Minor = minor;
        Patch = patch;
        Flags = flags;
        End = end;
        Entries = entries;
    }

    /// <summary>Where the pack starts in the file (0 for a standalone pack).</summary>
    public long Start { get; }

    /// <summary>The pack is embedded in an executable.</summary>
    public bool Embedded { get; }

    /// <summary>Pack format version (1, 2 or 3).</summary>
    public uint Format { get; }

    /// <summary>Godot version the pack was made with.</summary>
    public uint Major { get; }

    public uint Minor { get; }

    public uint Patch { get; }

    public uint Flags { get; }

    /// <summary>Where the pack's data ends (an embedded pack: where its size trailer starts; else the file's length).</summary>
    public long End { get; }

    /// <summary>The directory, in its order.</summary>
    public IReadOnlyList<GodotPackEntry> Entries { get; }

    /// <summary>Opens the pack at <paramref name="path"/> (a <c>.pck</c>, or an executable with one embedded).</summary>
    /// <exception cref="GodotFormatException">Not a pack, damaged, encrypted or of a format this code doesn't read.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    public static GodotPack Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            return Read(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Whether the file is a pack or holds one at its end (cheap: header and trailer only).</summary>
    public static bool IsPack(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return FindStart(stream) is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The bytes of <paramref name="entry"/>.</summary>
    /// <exception cref="GodotFormatException">The entry is larger than <paramref name="maxSize"/>.</exception>
    public byte[] Read(GodotPackEntry entry, long maxSize = 512L << 20)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size > maxSize)
        {
            throw new GodotFormatException(GodotProblem.TooLarge, $"{entry.Path} is too large to read ({entry.Size} bytes).");
        }

        var buffer = new byte[entry.Size];
        _stream.Position = entry.Offset;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Writes the whole file again into <paramref name="output"/>: an embedded pack's executable part as it is, then the
    /// pack in its own format and version with <paramref name="replacements"/> (by <see cref="GodotPackEntry.Path"/>) in
    /// place of those entries' contents (their MD5 updated) and every other entry copied byte for byte. An embedded
    /// pack is padded to at least its old length, so a Windows <c>pck</c> section still covers it, then gets its trailer.
    /// </summary>
    public void Write(Stream destination, IReadOnlyDictionary<string, byte[]> replacements, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(replacements);
        using var output = new CountingStream(destination);
        var buffer = new byte[1 << 20];
        if (Embedded)
        {
            Copy(0, Start, output, buffer, cancellationToken);
        }

        var sizes = Entries.Select(e => replacements.TryGetValue(e.Path, out var content) ? content.LongLength : e.Size).ToArray();
        var headerSize = Format switch { 1 => 20 + 64, 2 => 32 + 64, _ => 40 + 64 };
        var directorySize = 4 + Entries.Sum(e => 4L + e.RawPath.Length + 8 + 8 + 16 + (Format >= 2 ? 4 : 0));

        // Layout relative to the pack's start: formats 1 and 2 put the directory first, format 3 last.
        var fileBase = Align(Format == 3 ? headerSize : headerSize + directorySize);
        var offsets = new long[Entries.Count];
        var position = fileBase;
        for (var i = 0; i < Entries.Count; i++)
        {
            offsets[i] = position;
            position = Align(position + sizes[i]);
        }

        var directoryOffset = Format == 3 ? position : headerSize;
        var packStart = Embedded ? Start : 0;
        var header = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        header.Write(Magic);
        header.Write(Format);
        header.Write(Major);
        header.Write(Minor);
        header.Write(Patch);
        if (Format >= 2)
        {
            header.Write(Flags);
            header.Write((ulong)(Format == 3 || (Flags & RelativeFileBase) != 0 ? fileBase : packStart + fileBase));
        }

        if (Format == 3)
        {
            header.Write((ulong)directoryOffset);
        }

        header.Write(new byte[64]);
        if (Format != 3)
        {
            WriteDirectory(header, offsets, sizes, replacements, packStart, fileBase);
        }

        for (var i = 0; i < Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pad(header, packStart + offsets[i] - output.Position);
            if (replacements.TryGetValue(Entries[i].Path, out var content))
            {
                output.Write(content);
            }
            else
            {
                Copy(Entries[i].Offset, Entries[i].Size, output, buffer, cancellationToken);
            }
        }

        Pad(header, packStart + (Format == 3 ? directoryOffset : position) - output.Position);
        if (Format == 3)
        {
            WriteDirectory(header, offsets, sizes, replacements, packStart, fileBase);
        }

        if (Embedded)
        {
            var length = output.Position - packStart;
            var padded = Math.Max(length, End - Start);
            padded += (8 - (padded % 8)) % 8;
            Pad(header, padded - length);
            header.Write((ulong)padded);
            header.Write(Magic);
        }

        header.Flush();
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Counts what is written, so the output needn't be seekable.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _position;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _position += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            _position++;
        }
    }

    private void WriteDirectory(BinaryWriter writer, long[] offsets, long[] sizes, IReadOnlyDictionary<string, byte[]> replacements, long packStart, long fileBase)
    {
        writer.Write((uint)Entries.Count);
        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            writer.Write((uint)entry.RawPath.Length);
            writer.Write(entry.RawPath);

            // Format 1: from the start of the file; 2 and 3: from the file base.
            writer.Write((ulong)(Format == 1 ? packStart + offsets[i] : offsets[i] - fileBase));
            writer.Write((ulong)sizes[i]);
            writer.Write(replacements.TryGetValue(entry.Path, out var content) ? MD5.HashData(content) : entry.Md5);
            if (Format >= 2)
            {
                writer.Write(entry.Flags);
            }
        }
    }

    private void Copy(long offset, long size, Stream output, byte[] buffer, CancellationToken cancellationToken)
    {
        _stream.Position = offset;
        while (size > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, size));
            if (read == 0)
            {
                throw new GodotFormatException(GodotProblem.Corrupt, "The pack ends before its last file.");
            }

            output.Write(buffer, 0, read);
            size -= read;
        }
    }

    private static void Pad(BinaryWriter writer, long count)
    {
        for (; count > 0; count--)
        {
            writer.Write((byte)0);
        }
    }

    private static long Align(long value) => (value + Alignment - 1) / Alignment * Alignment;

    /// <summary>The pack's start and end, or <c>null</c> when the file is no pack.</summary>
    private static (long Start, long End, bool Embedded)? FindStart(Stream stream)
    {
        var length = stream.Length;
        Span<byte> word = stackalloc byte[12];
        if (length >= 4)
        {
            stream.Position = 0;
            stream.ReadExactly(word[..4]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(word) == Magic)
            {
                return (0, length, false);
            }
        }

        if (length < 12 + 4)
        {
            return null;
        }

        stream.Position = length - 12;
        stream.ReadExactly(word);
        if (BinaryPrimitives.ReadUInt32LittleEndian(word[8..]) != Magic)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt64LittleEndian(word);
        if (size > (ulong)(length - 12))
        {
            return null;
        }

        var start = length - 12 - (long)size;
        stream.Position = start;
        stream.ReadExactly(word[..4]);
        return BinaryPrimitives.ReadUInt32LittleEndian(word) == Magic ? (start, length - 12, true) : null;
    }

    private static GodotPack Read(FileStream stream)
    {
        if (FindStart(stream) is not var (start, end, embedded))
        {
            throw new GodotFormatException(GodotProblem.NotPack, "Not a Godot pack (no GDPC header).");
        }

        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            stream.Position = start + 4;
            var format = reader.ReadUInt32();
            var major = reader.ReadUInt32();
            var minor = reader.ReadUInt32();
            var patch = reader.ReadUInt32();
            if (format is < 1 or > 3)
            {
                throw new GodotFormatException(GodotProblem.Unsupported, $"Pack format {format} isn't one this app reads (Godot {major}.{minor}).");
            }

            uint flags = 0;
            long fileBase = 0;
            if (format >= 2)
            {
                flags = reader.ReadUInt32();
                fileBase = (long)reader.ReadUInt64();
                if (format == 3 || (flags & RelativeFileBase) != 0)
                {
                    fileBase += start;
                }
            }

            if ((flags & DirectoryEncrypted) != 0)
            {
                throw new GodotFormatException(GodotProblem.Encrypted, "The pack is encrypted with the developer's key, so its files can't be read.");
            }

            if ((flags & SparseBundle) != 0)
            {
                throw new GodotFormatException(GodotProblem.Unsupported, "The pack is a sparse bundle (files stored apart from it), which this app doesn't read.");
            }

            if (format == 3)
            {
                stream.Position = start + (long)reader.ReadUInt64();
            }
            else
            {
                stream.Position += 64;
            }

            var count = reader.ReadUInt32();
            if (count > MaxEntries)
            {
                throw new GodotFormatException(GodotProblem.Corrupt, $"The pack lists {count} files.");
            }

            var entries = new List<GodotPackEntry>((int)count);
            for (var i = 0; i < count; i++)
            {
                var pathLength = reader.ReadUInt32();
                if (pathLength > MaxPathLength)
                {
                    throw new GodotFormatException(GodotProblem.Corrupt, $"File {i} of the pack has a {pathLength}-byte name.");
                }

                var raw = reader.ReadBytes((int)pathLength);
                var offset = reader.ReadUInt64();
                var size = reader.ReadUInt64();
                var md5 = reader.ReadBytes(16);
                var entryFlags = format >= 2 ? reader.ReadUInt32() : 0;
                var absolute = format == 1 ? (long)offset : fileBase + (long)offset;
                if (raw.Length != pathLength || md5.Length != 16 || size > (ulong)end || absolute < start || absolute > end - (long)size)
                {
                    throw new GodotFormatException(GodotProblem.Corrupt, $"File {i} of the pack lies outside it.");
                }

                var nul = Array.IndexOf(raw, (byte)0);
                var name = Encoding.UTF8.GetString(raw, 0, nul < 0 ? raw.Length : nul);
                entries.Add(new GodotPackEntry(Normalize(name), absolute, (long)size, md5, entryFlags, raw));
            }

            return new GodotPack(stream, start, embedded, format, major, minor, patch, flags, end, entries);
        }
        catch (EndOfStreamException e)
        {
            throw new GodotFormatException(GodotProblem.Corrupt, "The pack's directory is cut short.", e);
        }
    }

    /// <summary><c>res://a/b.tscn</c> (formats 1, 2) and <c>a/b.tscn</c> (format 3) → <c>a/b.tscn</c>.</summary>
    internal static string Normalize(string path) => (path.StartsWith("res://", StringComparison.Ordinal) ? path[6..] : path).TrimStart('/');
}

/// <summary>A file of a <see cref="GodotPack"/>.</summary>
/// <param name="Path">The resource path without <c>res://</c> (<c>ui/menu.tscn</c>).</param>
/// <param name="Offset">Where its bytes start in the file holding the pack.</param>
/// <param name="RawPath">The name as stored (with its zero padding), written back as is.</param>
public sealed record GodotPackEntry(string Path, long Offset, long Size, byte[] Md5, uint Flags, byte[] RawPath)
{
    /// <summary>Encrypted with the developer's key (Godot 4): copied, never read.</summary>
    public bool Encrypted => (Flags & 1) != 0;

    /// <summary>A removal marker of a patch pack (Godot 4.4+): no data.</summary>
    public bool Removal => (Flags & 2) != 0;
}

/// <summary>Why a Godot file can't be read.</summary>
public enum GodotProblem
{
    NotPack,
    Corrupt,
    Encrypted,
    Unsupported,
    TooLarge,
}

/// <summary>A Godot pack or resource that can't be read.</summary>
public sealed class GodotFormatException : Exception
{
    public GodotFormatException(GodotProblem problem, string message, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
    }

    public GodotFormatException()
    {
    }

    public GodotFormatException(string message)
        : base(message)
    {
    }

    public GodotFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public GodotProblem Problem { get; }
}
