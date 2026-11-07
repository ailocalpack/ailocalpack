namespace AiLocalPack.Core.Patches;

/// <summary>
/// Decodes the xz / LZMA2 stream xdelta3's <c>lzma</c> secondary compressor writes for one kind of VCDIFF section (data,
/// instructions or addresses). xdelta3 keeps one compressor per kind for the whole delta and flushes it at the end of
/// every window's section, so the first compressed section starts the xz stream (stream header, block header) and the
/// later ones continue it: the dictionary and the LZMA state carry over, and each section is the chunks that give its
/// decoded size. There is no index or footer to read; only one block with the single LZMA2 filter is accepted. The
/// result is checked by the window's Adler-32 and the file's SHA-256, so the xz checks are not computed. Damage throws
/// <see cref="VcdiffException"/> (<see cref="VcdiffProblem.Corrupt"/>; a dictionary over the window limit
/// <see cref="VcdiffProblem.TooLarge"/>).
/// </summary>
internal sealed class Lzma2Decoder(int maxDictionary)
{
    private const byte Lzma2FilterId = 0x21;

    private LzmaState? _lzma;
    private bool _needDictionaryReset = true;
    private bool _needProperties = true;

    private static ReadOnlySpan<byte> XzMagic => [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00];

    /// <summary>The next section: <paramref name="input"/> decoded into exactly <paramref name="size"/> bytes.</summary>
    public byte[] Decode(ReadOnlySpan<byte> input, int size)
    {
        var position = 0;
        if (_lzma is null)
        {
            _lzma = new LzmaState(ReadHeaders(input, ref position, maxDictionary));
        }

        var output = new byte[size];
        var lzma = _lzma;
        lzma.Begin(output);
        while (lzma.Written < size)
        {
            if (position >= input.Length)
            {
                throw Corrupt("the LZMA2 data ends early");
            }

            var control = input[position++];
            if (control == 0x00)
            {
                throw Corrupt("the LZMA2 data ends early");
            }

            if (control is 0x01 or 0x02)
            {
                if (position + 2 > input.Length)
                {
                    throw Corrupt("the LZMA2 data ends early");
                }

                var length = ((input[position] << 8) | input[position + 1]) + 1;
                position += 2;
                if (control == 0x01)
                {
                    lzma.ResetDictionary();
                    _needDictionaryReset = false;
                }
                else if (_needDictionaryReset)
                {
                    throw Corrupt("the LZMA2 data doesn't start with a dictionary reset");
                }

                if (position + length > input.Length || lzma.Written + length > size)
                {
                    throw Corrupt("an LZMA2 chunk is longer than its data");
                }

                lzma.CopyUncompressed(input.Slice(position, length));
                position += length;
                continue;
            }

            if (control < 0x80)
            {
                throw Corrupt($"0x{control:x2} is not an LZMA2 chunk");
            }

            if (position + 4 > input.Length)
            {
                throw Corrupt("the LZMA2 data ends early");
            }

            var unpacked = ((control & 0x1F) << 16) + (input[position] << 8) + input[position + 1] + 1;
            var packed = (input[position + 2] << 8) + input[position + 3] + 1;
            position += 4;
            var reset = (control >> 5) & 0x03;
            if (reset == 3)
            {
                lzma.ResetDictionary();
                _needDictionaryReset = false;
            }
            else if (_needDictionaryReset)
            {
                throw Corrupt("the LZMA2 data doesn't start with a dictionary reset");
            }

            if (reset >= 2)
            {
                if (position >= input.Length)
                {
                    throw Corrupt("the LZMA2 data ends early");
                }

                lzma.SetProperties(input[position++]);
                _needProperties = false;
            }
            else if (_needProperties)
            {
                throw Corrupt("an LZMA2 chunk comes before the LZMA properties");
            }

            if (reset >= 1)
            {
                lzma.ResetState();
            }

            if (position + packed > input.Length || lzma.Written + unpacked > size)
            {
                throw Corrupt("an LZMA2 chunk is longer than its data");
            }

            lzma.DecodeChunk(input.Slice(position, packed), unpacked);
            position += packed;
        }

        return output;
    }

    /// <summary>The xz stream header and the block header; returns the dictionary size.</summary>
    private static int ReadHeaders(ReadOnlySpan<byte> input, ref int position, int maxDictionary)
    {
        // Stream header: magic, two flag bytes, CRC32.
        if (input.Length < 12 || !input[..6].SequenceEqual(XzMagic) || input[6] != 0 || input[7] > 0x0F)
        {
            throw Corrupt("the lzma section is not an xz stream");
        }

        position = 12;

        // Block header: size byte ((n + 1) * 4 bytes in all), flags, optional sizes, filter flags, padding, CRC32.
        if (position >= input.Length || input[position] == 0)
        {
            throw Corrupt("the xz stream has no block");
        }

        var headerSize = (input[position] + 1) * 4;
        if (position + headerSize > input.Length)
        {
            throw Corrupt("the xz block header is cut off");
        }

        var header = input.Slice(position, headerSize - 4);
        var flags = header[1];
        if ((flags & 0x3F) != 0)
        {
            throw Corrupt("the xz block uses filters other than LZMA2");
        }

        var at = 2;
        if ((flags & 0x40) != 0)
        {
            ReadXzNumber(header, ref at);
        }

        if ((flags & 0x80) != 0)
        {
            ReadXzNumber(header, ref at);
        }

        if (ReadXzNumber(header, ref at) != Lzma2FilterId || ReadXzNumber(header, ref at) != 1 || at >= header.Length)
        {
            throw Corrupt("the xz block uses filters other than LZMA2");
        }

        var property = header[at];
        if (property > 40)
        {
            throw Corrupt("the LZMA2 dictionary size is invalid");
        }

        var dictionary = property == 40 ? uint.MaxValue : (2u | (property & 1u)) << ((property / 2) + 11);
        if (dictionary > (uint)maxDictionary)
        {
            throw new VcdiffException(VcdiffProblem.TooLarge, $"The patch's LZMA2 dictionary of {dictionary} bytes is larger than the limit of {maxDictionary}.");
        }

        position += headerSize;
        return (int)dictionary;
    }

    private static ulong ReadXzNumber(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 63; shift += 7)
        {
            if (position >= data.Length)
            {
                throw Corrupt("the xz block header is cut off");
            }

            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw Corrupt("a number in the xz block header is too long");
    }

    internal static VcdiffException Corrupt(string detail) => new(VcdiffProblem.Corrupt, $"The patch is damaged: {detail}.");

    /// <summary>
    /// The LZMA decoder: state carried across LZMA2 chunks and sections; a circular dictionary of the stream's size, every
    /// byte also written to the current section's output.
    /// </summary>
    private sealed class LzmaState(int dictionarySize)
    {
        private const int States = 12;
        private const int PositionStatesMax = 1 << 4;
        private const int LengthToPositionStates = 4;
        private const int AlignBits = 4;
        private const int EndPositionModelIndex = 14;
        private const int FullDistances = 1 << (EndPositionModelIndex >> 1);
        private const int MatchMinLength = 2;
        private const ushort ProbabilityInit = 1024;

        private readonly byte[] _dictionary = new byte[dictionarySize];
        private readonly ushort[] _isMatch = new ushort[States * PositionStatesMax];
        private readonly ushort[] _isRep = new ushort[States];
        private readonly ushort[] _isRepG0 = new ushort[States];
        private readonly ushort[] _isRepG1 = new ushort[States];
        private readonly ushort[] _isRepG2 = new ushort[States];
        private readonly ushort[] _isRep0Long = new ushort[States * PositionStatesMax];
        private readonly ushort[] _positionSlot = new ushort[LengthToPositionStates << 6];
        private readonly ushort[] _positionSpecial = new ushort[FullDistances - EndPositionModelIndex];
        private readonly ushort[] _align = new ushort[1 << AlignBits];
        private readonly LengthDecoder _length = new();
        private readonly LengthDecoder _repLength = new();
        private ushort[] _literal = [];
        private int _lc;
        private int _lp;
        private int _pb;
        private int _state;
        private uint _rep0;
        private uint _rep1;
        private uint _rep2;
        private uint _rep3;

        /// <summary>Next write position in the circular dictionary.</summary>
        private int _at;

        /// <summary>Bytes since the last dictionary reset (positions for the literal and position states).</summary>
        private long _total;

        private byte[] _output = [];

        /// <summary>Bytes written to the current section's output.</summary>
        public int Written { get; private set; }

        public void Begin(byte[] output)
        {
            _output = output;
            Written = 0;
        }

        public void ResetDictionary()
        {
            _at = 0;
            _total = 0;
        }

        public void SetProperties(byte properties)
        {
            if (properties >= 9 * 5 * 5)
            {
                throw Corrupt("the LZMA properties are invalid");
            }

            _lc = properties % 9;
            _lp = properties / 9 % 5;
            _pb = properties / 45;
            if (_lc + _lp > 4)
            {
                throw Corrupt("the LZMA2 properties are invalid");
            }

            _literal = new ushort[0x300 << (_lc + _lp)];
        }

        public void ResetState()
        {
            Array.Fill(_isMatch, ProbabilityInit);
            Array.Fill(_isRep, ProbabilityInit);
            Array.Fill(_isRepG0, ProbabilityInit);
            Array.Fill(_isRepG1, ProbabilityInit);
            Array.Fill(_isRepG2, ProbabilityInit);
            Array.Fill(_isRep0Long, ProbabilityInit);
            Array.Fill(_positionSlot, ProbabilityInit);
            Array.Fill(_positionSpecial, ProbabilityInit);
            Array.Fill(_align, ProbabilityInit);
            Array.Fill(_literal, ProbabilityInit);
            _length.Reset();
            _repLength.Reset();
            _state = 0;
            _rep0 = _rep1 = _rep2 = _rep3 = 0;
        }

        public void CopyUncompressed(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                Put(value);
            }
        }

        public void DecodeChunk(ReadOnlySpan<byte> packed, int unpacked)
        {
            var range = new RangeDecoder(packed);
            var end = Written + unpacked;
            var positionMask = (1 << _pb) - 1;
            var literalPositionMask = (1 << _lp) - 1;
            while (Written < end)
            {
                var positionState = (int)(_total & positionMask);
                if (range.Bit(ref _isMatch[(_state << 4) + positionState]) == 0)
                {
                    var previous = _total > 0 ? Back(0) : (byte)0;
                    var offset = 0x300 * ((((int)_total & literalPositionMask) << _lc) + (previous >> (8 - _lc)));
                    var symbol = 1;
                    if (_state >= 7)
                    {
                        var matchByte = (int)Back(_rep0);
                        while (symbol < 0x100)
                        {
                            var matchBit = (matchByte >> 7) & 1;
                            matchByte <<= 1;
                            var bit = range.Bit(ref _literal[offset + ((1 + matchBit) << 8) + symbol]);
                            symbol = (symbol << 1) | bit;
                            if (matchBit != bit)
                            {
                                break;
                            }
                        }
                    }

                    while (symbol < 0x100)
                    {
                        symbol = (symbol << 1) | range.Bit(ref _literal[offset + symbol]);
                    }

                    Put((byte)symbol);
                    _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                    continue;
                }

                int length;
                if (range.Bit(ref _isRep[_state]) == 1)
                {
                    if (_total == 0)
                    {
                        throw Corrupt("an LZMA match points before the data");
                    }

                    if (range.Bit(ref _isRepG0[_state]) == 0)
                    {
                        if (range.Bit(ref _isRep0Long[(_state << 4) + positionState]) == 0)
                        {
                            _state = _state < 7 ? 9 : 11;
                            Put(Back(_rep0));
                            continue;
                        }
                    }
                    else
                    {
                        uint distance;
                        if (range.Bit(ref _isRepG1[_state]) == 0)
                        {
                            distance = _rep1;
                        }
                        else
                        {
                            if (range.Bit(ref _isRepG2[_state]) == 0)
                            {
                                distance = _rep2;
                            }
                            else
                            {
                                distance = _rep3;
                                _rep3 = _rep2;
                            }

                            _rep2 = _rep1;
                        }

                        _rep1 = _rep0;
                        _rep0 = distance;
                    }

                    length = _repLength.Decode(ref range, positionState);
                    _state = _state < 7 ? 8 : 11;
                }
                else
                {
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    length = _length.Decode(ref range, positionState);
                    _state = _state < 7 ? 7 : 10;
                    _rep0 = DecodeDistance(ref range, length);
                    if (_rep0 == uint.MaxValue)
                    {
                        throw Corrupt("an LZMA2 chunk has an end marker");
                    }
                }

                length += MatchMinLength;
                if (Written + length > end)
                {
                    throw Corrupt("an LZMA match runs past its chunk");
                }

                for (var i = 0; i < length; i++)
                {
                    Put(Back(_rep0));
                }
            }

            range.EnsureFinished();
        }

        /// <summary>The byte <paramref name="distance"/> + 1 back.</summary>
        private byte Back(uint distance)
        {
            if (distance >= _total || distance >= (uint)_dictionary.Length)
            {
                throw Corrupt("an LZMA match points before the data");
            }

            var index = _at - (int)distance - 1;
            return _dictionary[index < 0 ? index + _dictionary.Length : index];
        }

        private void Put(byte value)
        {
            _dictionary[_at] = value;
            _at = _at + 1 == _dictionary.Length ? 0 : _at + 1;
            _total++;
            _output[Written++] = value;
        }

        private uint DecodeDistance(ref RangeDecoder range, int length)
        {
            var lengthState = Math.Min(length, LengthToPositionStates - 1);
            var slot = range.Tree(_positionSlot.AsSpan(lengthState << 6, 64), 6);
            if (slot < 4)
            {
                return (uint)slot;
            }

            var directBits = (slot >> 1) - 1;
            var distance = (uint)((2 | (slot & 1)) << directBits);
            if (slot < EndPositionModelIndex)
            {
                distance += (uint)range.ReverseTree(_positionSpecial, (int)distance - slot - 1, directBits);
            }
            else
            {
                distance += range.Direct(directBits - AlignBits) << AlignBits;
                distance += (uint)range.ReverseTree(_align, 0, AlignBits);
            }

            return distance;
        }
    }

    private sealed class LengthDecoder
    {
        private const int LowBits = 3;
        private const int MidBits = 3;
        private const int HighBits = 8;
        private readonly ushort[] _choice = new ushort[2];
        private readonly ushort[] _low = new ushort[16 << LowBits];
        private readonly ushort[] _mid = new ushort[16 << MidBits];
        private readonly ushort[] _high = new ushort[1 << HighBits];

        public void Reset()
        {
            Array.Fill(_choice, (ushort)1024);
            Array.Fill(_low, (ushort)1024);
            Array.Fill(_mid, (ushort)1024);
            Array.Fill(_high, (ushort)1024);
        }

        public int Decode(ref RangeDecoder range, int positionState)
        {
            if (range.Bit(ref _choice[0]) == 0)
            {
                return range.Tree(_low.AsSpan(positionState << LowBits, 1 << LowBits), LowBits);
            }

            if (range.Bit(ref _choice[1]) == 0)
            {
                return (1 << LowBits) + range.Tree(_mid.AsSpan(positionState << MidBits, 1 << MidBits), MidBits);
            }

            return (1 << LowBits) + (1 << MidBits) + range.Tree(_high, HighBits);
        }
    }

    /// <summary>The LZMA range decoder over one LZMA2 chunk.</summary>
    private ref struct RangeDecoder
    {
        private const int ModelBits = 11;
        private const uint TopValue = 1 << 24;
        private readonly ReadOnlySpan<byte> _input;
        private int _position;
        private uint _range;
        private uint _code;

        public RangeDecoder(ReadOnlySpan<byte> input)
        {
            if (input.Length < 5 || input[0] != 0)
            {
                throw Corrupt("an LZMA chunk doesn't start like one");
            }

            _input = input;
            _range = uint.MaxValue;
            for (_position = 1; _position < 5; _position++)
            {
                _code = (_code << 8) | input[_position];
            }

            if (_code == _range)
            {
                throw Corrupt("an LZMA chunk doesn't start like one");
            }
        }

        public int Bit(ref ushort probability)
        {
            var bound = (_range >> ModelBits) * probability;
            int bit;
            if (_code < bound)
            {
                _range = bound;
                probability += (ushort)(((1 << ModelBits) - probability) >> 5);
                bit = 0;
            }
            else
            {
                _range -= bound;
                _code -= bound;
                probability -= (ushort)(probability >> 5);
                bit = 1;
            }

            Normalize();
            return bit;
        }

        public uint Direct(int count)
        {
            uint result = 0;
            for (var i = 0; i < count; i++)
            {
                _range >>= 1;
                uint bit = 0;
                if (_code >= _range)
                {
                    _code -= _range;
                    bit = 1;
                }

                result = (result << 1) | bit;
                Normalize();
            }

            return result;
        }

        public int Tree(Span<ushort> probabilities, int bits)
        {
            var m = 1;
            for (var i = 0; i < bits; i++)
            {
                m = (m << 1) | Bit(ref probabilities[m]);
            }

            return m - (1 << bits);
        }

        /// <summary>A reverse bit tree whose node <c>m</c> (from 1) is <c>probabilities[offset + m]</c>.</summary>
        public int ReverseTree(ushort[] probabilities, int offset, int bits)
        {
            var m = 1;
            var symbol = 0;
            for (var i = 0; i < bits; i++)
            {
                var bit = Bit(ref probabilities[offset + m]);
                m = (m << 1) | bit;
                symbol |= bit << i;
            }

            return symbol;
        }

        public readonly void EnsureFinished()
        {
            if (_position > _input.Length)
            {
                throw Corrupt("an LZMA chunk is cut off");
            }
        }

        private void Normalize()
        {
            if (_range >= TopValue)
            {
                return;
            }

            _range <<= 8;
            // Reading past the chunk counts as damage (checked at its end); zeros keep the arithmetic defined meanwhile.
            _code = (_code << 8) | (_position < _input.Length ? _input[_position] : (byte)0);
            _position++;
        }
    }
}
