using System.Buffers.Binary;

namespace AiLocalPack.Core.Patches;

/// <summary>Why a VCDIFF delta can't be applied.</summary>
public enum VcdiffProblem
{
    /// <summary>The file is not a VCDIFF delta (no <c>D6 C3 C4</c> header).</summary>
    NotVcdiff,

    /// <summary>
    /// A valid delta using something this decoder doesn't read: another VCDIFF version, an application-defined code
    /// table, or xdelta3's <c>djw</c> / <c>fgk</c> secondary compressors (only <c>lzma</c>, xdelta3's default, and none are read).
    /// </summary>
    Unsupported,

    /// <summary>The delta is damaged or cut off.</summary>
    Corrupt,

    /// <summary>The delta copies from beyond the end of the source file: it was made from another file.</summary>
    SourceTooShort,

    /// <summary>A window's Adler-32 (xdelta3 writes one per window) doesn't match what was decoded: the source is not the one the delta was made from.</summary>
    ChecksumMismatch,

    /// <summary>The result would be larger than <see cref="VcdiffOptions.MaxTargetSize"/> (or a window larger than <see cref="VcdiffOptions.MaxWindowSize"/>).</summary>
    TooLarge,
}

/// <summary>A VCDIFF delta that can't be applied; nothing about the target is guaranteed then (decode into a temp file).</summary>
public sealed class VcdiffException(VcdiffProblem problem, string message) : Exception(message)
{
    public VcdiffProblem Problem { get; } = problem;
}

/// <param name="MaxTargetSize">Largest result accepted (a tiny delta can describe a huge file).</param>
/// <param name="MaxWindowSize">Largest target window and section (xdelta3 writes at most 64 MiB, its hard limit).</param>
public sealed record VcdiffOptions(long MaxTargetSize = 64L << 30, int MaxWindowSize = 64 << 20)
{
    public static VcdiffOptions Default { get; } = new();
}

/// <summary>
/// Decodes VCDIFF deltas (RFC 3284) as xdelta3 writes them: the default code table, source windows from the source file
/// (<c>VCD_SOURCE</c>) or from the target written so far (<c>VCD_TARGET</c>), the application header skipped, xdelta3's
/// per-window Adler-32 (<c>VCD_ADLER32</c>, window indicator bit 2) checked, and its secondary compression of the data,
/// instruction and address sections read for <c>lzma</c> (xdelta3's default: the decoded size, then the next part of one xz stream per kind of
/// section, <see cref="Lzma2Decoder"/>). The
/// delta is read once, front to back (a ZIP entry stream will do); the source is read at random (seekable); the target
/// is written front to back (and must be readable and seekable for <c>VCD_TARGET</c> windows). Every failure is a
/// <see cref="VcdiffException"/> with a <see cref="VcdiffProblem"/>; IO errors of the streams pass through.
/// </summary>
public static class VcdiffDecoder
{
    private const byte SourceWindow = 0x01;
    private const byte TargetWindow = 0x02;
    private const byte Adler32Window = 0x04;
    private const byte HeaderSecondary = 0x01;
    private const byte HeaderCodeTable = 0x02;
    private const byte HeaderApplication = 0x04;
    private const byte DjwId = 1;
    private const byte LzmaId = 2;
    private const byte FgkId = 16;
    private const int MaxApplicationHeader = 1 << 20;

    /// <summary>Applies <paramref name="delta"/> to <paramref name="source"/> (may be <c>null</c> for a delta without source windows), writing the result to <paramref name="target"/>; returns its length.</summary>
    public static long Decode(Stream? source, Stream delta, Stream target, VcdiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(target);
        options ??= VcdiffOptions.Default;
        if (source is { CanSeek: false })
        {
            throw new ArgumentException("The source must be seekable.", nameof(source));
        }

        var input = new DeltaReader(delta);
        var secondary = ReadHeader(input);
        var cache = new AddressCache();
        var decompressors = new Lzma2Decoder?[3];
        var targetStart = target.CanSeek ? target.Position : 0;
        long written = 0;
        while (input.TryReadByte(out var indicator))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = DecodeWindow(input, indicator, secondary, source, target, targetStart, written, cache, decompressors, options);
            if (written + window.Length > options.MaxTargetSize)
            {
                throw new VcdiffException(VcdiffProblem.TooLarge, $"The patched file would be larger than {options.MaxTargetSize} bytes.");
            }

            target.Write(window);
            written += window.Length;
        }

        return written;
    }

    private static byte ReadHeader(DeltaReader input)
    {
        Span<byte> magic = stackalloc byte[4];
        if (!input.TryReadExactly(magic) || magic[0] != 0xD6 || magic[1] != 0xC3 || magic[2] != 0xC4)
        {
            throw new VcdiffException(VcdiffProblem.NotVcdiff, "The patch is not a VCDIFF delta.");
        }

        if (magic[3] != 0)
        {
            throw new VcdiffException(VcdiffProblem.Unsupported, $"The patch uses VCDIFF version 0x{magic[3]:x2}; only version 0 (RFC 3284, as xdelta3 writes) is read.");
        }

        var indicator = input.ReadByte();
        if ((indicator & ~(HeaderSecondary | HeaderCodeTable | HeaderApplication)) != 0)
        {
            throw Corrupt("unknown header flags");
        }

        byte secondary = 0;
        if ((indicator & HeaderSecondary) != 0)
        {
            secondary = input.ReadByte();
            switch (secondary)
            {
                case LzmaId:
                    break;
                case DjwId or FgkId:
                    throw new VcdiffException(
                        VcdiffProblem.Unsupported,
                        $"The patch uses xdelta3's {(secondary == DjwId ? "djw" : "fgk")} secondary compression; make it with -S lzma (the default) or -S none.");
                default:
                    throw new VcdiffException(VcdiffProblem.Unsupported, $"The patch uses an unknown secondary compressor ({secondary}).");
            }
        }

        if ((indicator & HeaderCodeTable) != 0)
        {
            throw new VcdiffException(VcdiffProblem.Unsupported, "The patch uses its own code table; only the default one is read.");
        }

        if ((indicator & HeaderApplication) != 0)
        {
            var length = input.ReadVarint();
            if (length > MaxApplicationHeader)
            {
                throw Corrupt("the application header is too long");
            }

            input.Skip((int)length);
        }

        return secondary;
    }

    private static byte[] DecodeWindow(
        DeltaReader input, byte indicator, byte secondary, Stream? source, Stream target, long targetStart, long written, AddressCache cache, Lzma2Decoder?[] decompressors, VcdiffOptions options)
    {
        if ((indicator & ~(SourceWindow | TargetWindow | Adler32Window)) != 0 || (indicator & (SourceWindow | TargetWindow)) == (SourceWindow | TargetWindow))
        {
            throw Corrupt("invalid window flags");
        }

        long segmentLength = 0;
        long segmentPosition = 0;
        if ((indicator & (SourceWindow | TargetWindow)) != 0)
        {
            segmentLength = input.ReadVarint();
            segmentPosition = input.ReadVarint();
        }

        var encodingLength = input.ReadVarint();
        var start = input.Consumed;
        var targetLength = input.ReadVarint();
        if (targetLength > options.MaxWindowSize)
        {
            throw new VcdiffException(VcdiffProblem.TooLarge, $"A patch window of {targetLength} bytes is larger than the limit of {options.MaxWindowSize}.");
        }

        var deltaIndicator = input.ReadByte();
        if ((deltaIndicator & ~0x07) != 0 || (deltaIndicator != 0 && secondary == 0))
        {
            throw Corrupt("invalid section compression flags");
        }

        var dataLength = Section(input.ReadVarint(), options);
        var instructionLength = Section(input.ReadVarint(), options);
        var addressLength = Section(input.ReadVarint(), options);
        uint? adler = null;
        if ((indicator & Adler32Window) != 0)
        {
            Span<byte> checksum = stackalloc byte[4];
            input.ReadExactly(checksum);
            adler = BinaryPrimitives.ReadUInt32BigEndian(checksum);
        }

        var data = input.ReadBytes(dataLength);
        var instructions = input.ReadBytes(instructionLength);
        var addresses = input.ReadBytes(addressLength);
        if (input.Consumed - start != encodingLength)
        {
            throw Corrupt("a window's length doesn't match its contents");
        }

        if ((deltaIndicator & 0x01) != 0)
        {
            data = Decompress(data, ref decompressors[0], options);
        }

        if ((deltaIndicator & 0x02) != 0)
        {
            instructions = Decompress(instructions, ref decompressors[1], options);
        }

        if ((deltaIndicator & 0x04) != 0)
        {
            addresses = Decompress(addresses, ref decompressors[2], options);
        }

        var segment = new Segment(indicator, segmentPosition, segmentLength, source, target, targetStart, written);
        var window = new byte[targetLength];
        Execute(window, segment, data, instructions, addresses, cache);
        if (adler is { } expected && Adler32(window) != expected)
        {
            throw new VcdiffException(VcdiffProblem.ChecksumMismatch, "A patched part doesn't match its checksum: the file being patched is not the one the patch was made from.");
        }

        return window;
    }

    private static int Section(long length, VcdiffOptions options) =>
        length <= options.MaxWindowSize ? (int)length : throw new VcdiffException(VcdiffProblem.TooLarge, $"A patch section of {length} bytes is larger than the limit of {options.MaxWindowSize}.");

    /// <summary>
    /// An xdelta3 secondary-compressed section: the decoded size, then the next part of the xz stream of its kind of
    /// section (<paramref name="decompressor"/>, started by the first one).
    /// </summary>
    private static byte[] Decompress(byte[] section, ref Lzma2Decoder? decompressor, VcdiffOptions options)
    {
        var reader = new SectionReader(section, "compressed section");
        var size = reader.ReadVarint();
        if (size > options.MaxWindowSize)
        {
            throw new VcdiffException(VcdiffProblem.TooLarge, $"A patch section of {size} bytes is larger than the limit of {options.MaxWindowSize}.");
        }

        decompressor ??= new Lzma2Decoder(options.MaxWindowSize);
        return decompressor.Decode(section.AsSpan(reader.Position), (int)size);
    }

    private static void Execute(byte[] window, Segment segment, byte[] data, byte[] instructions, byte[] addresses, AddressCache cache)
    {
        var dataReader = new SectionReader(data, "data section");
        var instructionReader = new SectionReader(instructions, "instruction section");
        var addressReader = new SectionReader(addresses, "address section");
        cache.Reset();
        var position = 0;
        while (!instructionReader.AtEnd)
        {
            var code = CodeTable.Default[instructionReader.ReadByte()];
            for (var half = 0; half < 2; half++)
            {
                var (type, size, mode) = half == 0 ? (code.Type1, code.Size1, code.Mode1) : (code.Type2, code.Size2, code.Mode2);
                if (type == InstructionType.NoOp)
                {
                    continue;
                }

                long length = size == 0 ? instructionReader.ReadVarint() : size;
                if (length > window.Length - position)
                {
                    throw Corrupt("an instruction writes past its window");
                }

                var count = (int)length;
                switch (type)
                {
                    case InstructionType.Add:
                        dataReader.ReadBytes(count).CopyTo(window.AsSpan(position, count));
                        break;
                    case InstructionType.Run:
                        window.AsSpan(position, count).Fill(dataReader.ReadByte());
                        break;
                    default:
                        var here = segment.Length + position;
                        var address = cache.Decode(here, mode, ref addressReader);
                        Copy(window, position, address, count, segment);
                        break;
                }

                position += count;
            }
        }

        if (position != window.Length || !dataReader.AtEnd || !addressReader.AtEnd)
        {
            throw Corrupt("a window's instructions don't fill it exactly");
        }
    }

    /// <summary>A COPY: from the source segment for addresses below its length, else from the window itself (may overlap: byte by byte).</summary>
    private static void Copy(byte[] window, int position, long address, int count, Segment segment)
    {
        var done = 0;
        if (address < segment.Length)
        {
            var fromSegment = (int)Math.Min(count, segment.Length - address);
            segment.Read(address, window.AsSpan(position, fromSegment));
            done = fromSegment;
        }

        for (; done < count; done++)
        {
            var from = address + done - segment.Length;
            if (from >= position + done)
            {
                throw Corrupt("a copy reads ahead of what was decoded");
            }

            window[position + done] = window[from];
        }
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

    internal static VcdiffException Corrupt(string detail) => Lzma2Decoder.Corrupt(detail);

    /// <summary>The bytes a window copies from: part of the source file, or of the target already written.</summary>
    private sealed class Segment
    {
        private readonly Stream? _stream;
        private readonly long _start;

        public Segment(byte indicator, long position, long length, Stream? source, Stream target, long targetStart, long written)
        {
            Length = length;
            if ((indicator & SourceWindow) != 0)
            {
                if (source is null || position + length > source.Length || position < 0)
                {
                    throw new VcdiffException(VcdiffProblem.SourceTooShort, "The patch copies from beyond the end of the file being patched: it was made from another file.");
                }

                _stream = source;
                _start = position;
            }
            else if ((indicator & TargetWindow) != 0)
            {
                if (!target.CanRead || !target.CanSeek)
                {
                    throw new VcdiffException(VcdiffProblem.Unsupported, "The patch copies from its own output, which this target can't read back.");
                }

                if (position + length > written || position < 0)
                {
                    throw Corrupt("a window copies from output not written yet");
                }

                _stream = target;
                _start = targetStart + position;
            }
        }

        public long Length { get; }

        public void Read(long offset, Span<byte> destination)
        {
            var stream = _stream!;
            var resume = stream.Position;
            stream.Position = _start + offset;
            stream.ReadExactly(destination);
            if (ReferenceEquals(stream, _stream) && stream.CanWrite)
            {
                // The target: writing goes on at its end.
                stream.Position = resume;
            }
        }
    }

    /// <summary>The address cache of RFC 3284 §5.1 (near cache of 4, same cache of 3 × 256), reset for every window.</summary>
    private sealed class AddressCache
    {
        private const int NearSize = 4;
        private const int SameSize = 3;
        private readonly long[] _near = new long[NearSize];
        private readonly long[] _same = new long[SameSize * 256];
        private int _nextSlot;

        public void Reset()
        {
            Array.Clear(_near);
            Array.Clear(_same);
            _nextSlot = 0;
        }

        public long Decode(long here, byte mode, ref SectionReader addresses)
        {
            long address;
            if (mode == 0)
            {
                address = addresses.ReadVarint();
            }
            else if (mode == 1)
            {
                address = here - addresses.ReadVarint();
            }
            else if (mode < 2 + NearSize)
            {
                address = _near[mode - 2] + addresses.ReadVarint();
            }
            else
            {
                address = _same[((mode - (2 + NearSize)) * 256) + addresses.ReadByte()];
            }

            if (address < 0 || address >= here)
            {
                throw Corrupt("a copy address is out of range");
            }

            _near[_nextSlot] = address;
            _nextSlot = (_nextSlot + 1) % NearSize;
            _same[address % (SameSize * 256)] = address;
            return address;
        }
    }

    /// <summary>Reads a section held in memory.</summary>
    private ref struct SectionReader(byte[] data, string name)
    {
        public int Position { get; private set; }

        public readonly bool AtEnd => Position >= data.Length;

        public byte ReadByte() => Position < data.Length ? data[Position++] : throw Corrupt($"the {name} ends early");

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count > data.Length - Position)
            {
                throw Corrupt($"the {name} ends early");
            }

            var span = data.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public long ReadVarint()
        {
            long value = 0;
            for (var i = 0; i < 9; i++)
            {
                var b = ReadByte();
                value = (value << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw Corrupt($"a number in the {name} is too long");
        }
    }

    /// <summary>Reads the delta stream front to back, counting the bytes.</summary>
    private sealed class DeltaReader(Stream stream)
    {
        public long Consumed { get; private set; }

        public bool TryReadByte(out byte value)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                value = 0;
                return false;
            }

            Consumed++;
            value = (byte)b;
            return true;
        }

        public byte ReadByte() => TryReadByte(out var value) ? value : throw Corrupt("the patch ends early");

        public bool TryReadExactly(Span<byte> buffer)
        {
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            Consumed += read;
            return read == buffer.Length;
        }

        public void ReadExactly(Span<byte> buffer)
        {
            if (!TryReadExactly(buffer))
            {
                throw Corrupt("the patch ends early");
            }
        }

        public byte[] ReadBytes(int count)
        {
            var buffer = new byte[count];
            ReadExactly(buffer);
            return buffer;
        }

        public void Skip(int count)
        {
            Span<byte> buffer = stackalloc byte[4096];
            while (count > 0)
            {
                var part = Math.Min(count, buffer.Length);
                ReadExactly(buffer[..part]);
                count -= part;
            }
        }

        public long ReadVarint()
        {
            long value = 0;
            for (var i = 0; i < 9; i++)
            {
                var b = ReadByte();
                value = (value << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw Corrupt("a number in the patch is too long");
        }
    }

    private enum InstructionType : byte
    {
        NoOp,
        Add,
        Run,
        Copy,
    }

    private readonly record struct Code(InstructionType Type1, byte Size1, byte Mode1, InstructionType Type2, byte Size2, byte Mode2);

    /// <summary>The default code table of RFC 3284 §5.6.</summary>
    private static class CodeTable
    {
        public static readonly Code[] Default = Build();

        private static Code[] Build()
        {
            var table = new List<Code>(256)
            {
                new(InstructionType.Run, 0, 0, InstructionType.NoOp, 0, 0),
            };
            for (byte size = 0; size <= 17; size++)
            {
                table.Add(new Code(InstructionType.Add, size, 0, InstructionType.NoOp, 0, 0));
            }

            for (byte mode = 0; mode <= 8; mode++)
            {
                table.Add(new Code(InstructionType.Copy, 0, mode, InstructionType.NoOp, 0, 0));
                for (byte size = 4; size <= 18; size++)
                {
                    table.Add(new Code(InstructionType.Copy, size, mode, InstructionType.NoOp, 0, 0));
                }
            }

            for (byte mode = 0; mode <= 5; mode++)
            {
                for (byte add = 1; add <= 4; add++)
                {
                    for (byte copy = 4; copy <= 6; copy++)
                    {
                        table.Add(new Code(InstructionType.Add, add, 0, InstructionType.Copy, copy, mode));
                    }
                }
            }

            for (byte mode = 6; mode <= 8; mode++)
            {
                for (byte add = 1; add <= 4; add++)
                {
                    table.Add(new Code(InstructionType.Add, add, 0, InstructionType.Copy, 4, mode));
                }
            }

            for (byte mode = 0; mode <= 8; mode++)
            {
                table.Add(new Code(InstructionType.Copy, 4, mode, InstructionType.Add, 1, 0));
            }

            return [.. table];
        }
    }
}
