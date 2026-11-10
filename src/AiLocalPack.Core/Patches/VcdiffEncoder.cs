using System.Buffers.Binary;

namespace AiLocalPack.Core.Patches;

/// <summary>Sizes the encoder works with (tests make them small to cover several windows and a sliding source segment).</summary>
/// <param name="WindowSize">Bytes of the target per window.</param>
/// <param name="MaxSegmentSize">Bytes of the source one window may copy from (the whole source when it is smaller).</param>
public sealed record VcdiffEncoderOptions(int WindowSize = 8 << 20, int MaxSegmentSize = 32 << 20)
{
    public static VcdiffEncoderOptions Default { get; } = new();
}

/// <summary>
/// Makes a VCDIFF delta (RFC 3284, version 0, default code table, no secondary compression: the patch archive deflates
/// it) that <see cref="VcdiffDecoder"/> and xdelta3 apply (task 57): the target in windows of
/// <see cref="VcdiffEncoderOptions.WindowSize"/>, each with xdelta3's Adler-32 of the window (bit 2), copying from one
/// segment of the source (the whole source up to <see cref="VcdiffEncoderOptions.MaxSegmentSize"/>, else a segment that
/// follows where the last match was found, so an insertion or deletion earlier in a large file keeps matching). Matching
/// is greedy: the source is indexed every 16 bytes, every target position is looked up, and a match is extended both
/// ways; anything shorter than 16 bytes is added as data. Deterministic: the same files give the same delta.
/// </summary>
public static class VcdiffEncoder
{
    private const int Block = 16;
    private const int MaxTableBits = 22;
    private const byte SourceWindow = 0x01;
    private const byte Adler32Window = 0x04;
    private const byte AddOpcode = 1;
    private const byte CopyOpcode = 19;

    /// <summary>Writes the delta from <paramref name="source"/> to <paramref name="target"/> into <paramref name="delta"/>.</summary>
    /// <param name="source">The original file (seekable, read in segments).</param>
    /// <param name="target">The new file (read front to back).</param>
    /// <exception cref="IOException">A stream can't be read or written.</exception>
    public static void Encode(Stream source, Stream target, Stream delta, VcdiffEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(delta);
        if (!source.CanSeek)
        {
            throw new ArgumentException("The source must be seekable.", nameof(source));
        }

        options ??= VcdiffEncoderOptions.Default;
        if (options.WindowSize < Block || options.MaxSegmentSize < Block)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Window and segment sizes must be at least 16 bytes.");
        }

        delta.Write([0xD6, 0xC3, 0xC4, 0x00, 0x00]);
        var sourceLength = source.Length;
        var window = new byte[options.WindowSize];
        SourceSegment? segment = null;
        long targetPosition = 0;
        long drift = 0;
        int read;
        while ((read = ReadFull(target, window)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = SegmentFor(sourceLength, targetPosition + drift, read, options.MaxSegmentSize);
            if (segment is null || segment.Position != wanted.Position || segment.Length != wanted.Length)
            {
                segment = wanted.Length == 0 ? new SourceSegment(0, []) : SourceSegment.Load(source, wanted.Position, (int)wanted.Length);
            }

            var encoded = EncodeWindow(window.AsSpan(0, read), segment, out var lastMatchSource, out var lastMatchTarget);
            if (lastMatchSource >= 0)
            {
                drift = segment.Position + lastMatchSource - (targetPosition + lastMatchTarget);
            }

            var indicator = (byte)((segment.Length > 0 ? SourceWindow : 0) | Adler32Window);
            delta.WriteByte(indicator);
            if (segment.Length > 0)
            {
                WriteVarint(delta, segment.Length);
                WriteVarint(delta, segment.Position);
            }

            WriteVarint(delta, encoded.Length);
            delta.Write(encoded);
            targetPosition += read;
        }
    }

    /// <summary>Encodes the files at <paramref name="sourcePath"/> → <paramref name="targetPath"/> into a delta file.</summary>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be read or written.</exception>
    public static void EncodeFile(string sourcePath, string targetPath, Stream delta, VcdiffEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Encode(source, target, delta, options, cancellationToken);
    }

    private static (long Position, long Length) SegmentFor(long sourceLength, long expected, int windowLength, int maxSegment)
    {
        if (sourceLength <= maxSegment)
        {
            return (0, sourceLength);
        }

        // Centred on where the window's bytes are expected in the source (the target position shifted by the last match).
        var start = expected - ((maxSegment - windowLength) / 2);
        start = Math.Clamp(start, 0, sourceLength - maxSegment);
        return (start, maxSegment);
    }

    /// <summary>The window's encoding (target length … addresses); the last match's start in the segment and the window, or -1.</summary>
    private static byte[] EncodeWindow(ReadOnlySpan<byte> window, SourceSegment segment, out long lastMatchSource, out long lastMatchTarget)
    {
        var data = new MemoryStream();
        var instructions = new MemoryStream();
        var addresses = new MemoryStream();
        lastMatchSource = -1;
        lastMatchTarget = -1;
        var source = segment.Data;
        var literalStart = 0;
        var position = 0;
        while (position + Block <= window.Length)
        {
            var candidate = segment.Find(Hash(window.Slice(position, Block)));
            if (candidate < 0 || !window.Slice(position, Block).SequenceEqual(source.AsSpan(candidate, Block)))
            {
                position++;
                continue;
            }

            // Extend backwards into the pending literal, then forwards.
            var back = 0;
            while (position - back > literalStart && candidate - back > 0 && window[position - back - 1] == source[candidate - back - 1])
            {
                back++;
            }

            var length = Block;
            while (position + length < window.Length && candidate + length < source.Length && window[position + length] == source[candidate + length])
            {
                length++;
            }

            var start = position - back;
            var from = candidate - back;
            Add(window[literalStart..start], data, instructions);
            instructions.WriteByte(CopyOpcode);
            WriteVarint(instructions, length + back);
            WriteVarint(addresses, from);
            lastMatchSource = from;
            lastMatchTarget = start;
            position = start + length + back;
            literalStart = position;
        }

        Add(window[literalStart..], data, instructions);
        var encoding = new MemoryStream();
        WriteVarint(encoding, window.Length);
        encoding.WriteByte(0);
        WriteVarint(encoding, data.Length);
        WriteVarint(encoding, instructions.Length);
        WriteVarint(encoding, addresses.Length);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(window));
        encoding.Write(adler);
        data.WriteTo(encoding);
        instructions.WriteTo(encoding);
        addresses.WriteTo(encoding);
        return encoding.ToArray();
    }

    private static void Add(ReadOnlySpan<byte> literal, MemoryStream data, MemoryStream instructions)
    {
        if (literal.IsEmpty)
        {
            return;
        }

        instructions.WriteByte(AddOpcode);
        WriteVarint(instructions, literal.Length);
        data.Write(literal);
    }

    private static ulong Hash(ReadOnlySpan<byte> block)
    {
        var a = BinaryPrimitives.ReadUInt64LittleEndian(block);
        var b = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        var mixed = (a * 0x9E3779B97F4A7C15UL) ^ (b * 0xC2B2AE3D27D4EB4FUL);
        return (mixed ^ (mixed >> 29)) * 0xBF58476D1CE4E5B9UL;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total;
    }

    private static void WriteVarint(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[10];
        var i = bytes.Length;
        bytes[--i] = (byte)(value & 0x7F);
        for (value >>= 7; value > 0; value >>= 7)
        {
            bytes[--i] = (byte)(0x80 | (value & 0x7F));
        }

        stream.Write(bytes[i..]);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint a = 1, b = 0;
        while (data.Length > 0)
        {
            var chunk = data[..Math.Min(data.Length, 5552)];
            foreach (var value in chunk)
            {
                a += value;
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
            data = data[chunk.Length..];
        }

        return (b << 16) | a;
    }

    /// <summary>A part of the source in memory with its 16-byte blocks indexed (the first block of each hash wins).</summary>
    private sealed class SourceSegment
    {
        private readonly int[]? _table;
        private readonly int _shift;

        public SourceSegment(long position, byte[] data)
        {
            Position = position;
            Data = data;
            if (data.Length >= Block)
            {
                // About two slots per block, so a small file doesn't allocate the large table.
                var bits = Math.Clamp(64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(data.Length / Block)) + 1, 8, MaxTableBits);
                _shift = 64 - bits;
                _table = new int[1 << bits];
                for (var at = data.Length - Block - (data.Length % Block); at >= 0; at -= Block)
                {
                    // Walked backwards, so the earliest block of a hash is the one kept.
                    _table[Hash(data.AsSpan(at, Block)) >> _shift] = at + 1;
                }
            }
        }

        public long Position { get; }

        public byte[] Data { get; }

        public long Length => Data.LongLength;

        public static SourceSegment Load(Stream source, long position, int length)
        {
            var data = new byte[length];
            source.Position = position;
            source.ReadExactly(data);
            return new SourceSegment(position, data);
        }

        /// <summary>A block start with this hash, or -1.</summary>
        public int Find(ulong hash) => _table is null ? -1 : _table[hash >> _shift] - 1;
    }
}
