using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AiLocalPack.Agents.Install;

/// <summary>A key or signature this reader can't take (damaged, or a format it doesn't read).</summary>
public sealed class OpenPgpException(string message) : Exception(message);

/// <summary>
/// An OpenPGP v4 RSA public key (RFC 4880 §5.5.2), the primary key of an ASCII-armored key block: enough to check a
/// vendor's release signature against a key pinned in the app (task 43). Subkeys, user IDs and certifications are not read.
/// </summary>
public sealed class OpenPgpPublicKey
{
    private readonly byte[] _modulus;
    private readonly byte[] _exponent;

    private OpenPgpPublicKey(string fingerprint, byte[] modulus, byte[] exponent)
    {
        Fingerprint = fingerprint;
        _modulus = modulus;
        _exponent = exponent;
    }

    /// <summary>The v4 fingerprint, 40 uppercase hex digits (SHA-1 of the key packet).</summary>
    public string Fingerprint { get; }

    /// <summary>The key ID: the last 16 hex digits of the fingerprint.</summary>
    public string KeyId => Fingerprint[^16..];

    /// <summary>Reads the first public key packet of an armored <c>PUBLIC KEY BLOCK</c>. Throws <see cref="OpenPgpException"/>.</summary>
    public static OpenPgpPublicKey Read(string armored)
    {
        ArgumentNullException.ThrowIfNull(armored);
        var data = OpenPgpArmor.Decode(armored, "PUBLIC KEY BLOCK");
        foreach (var (tag, body) in OpenPgpPackets.Read(data))
        {
            if (tag != 6)
            {
                continue;
            }

            if (body.Length < 6 || body[0] != 4)
            {
                throw new OpenPgpException("Only version 4 public keys are read.");
            }

            if (body[5] is not (1 or 2 or 3))
            {
                throw new OpenPgpException($"Public key algorithm {body[5]} isn't RSA.");
            }

            var position = 6;
            var modulus = OpenPgpPackets.Mpi(body, ref position);
            var exponent = OpenPgpPackets.Mpi(body, ref position);
            var framed = new byte[body.Length + 3];
            framed[0] = 0x99;
            BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(1), checked((ushort)body.Length));
            body.CopyTo(framed, 3);
#pragma warning disable CA5350 // SHA-1 is what defines a v4 fingerprint; it only names the key, the signature hash is SHA-2.
            var fingerprint = Convert.ToHexString(SHA1.HashData(framed));
#pragma warning restore CA5350
            return new OpenPgpPublicKey(fingerprint, modulus, exponent);
        }

        throw new OpenPgpException("The key block holds no public key.");
    }

    internal RSA CreateRsa() => RSA.Create(new RSAParameters { Modulus = _modulus, Exponent = _exponent });

    internal int ModulusLength => _modulus.Length;
}

/// <summary>What checking a signature found.</summary>
public enum OpenPgpVerdict
{
    /// <summary>The key signed exactly these bytes.</summary>
    Valid,

    /// <summary>The signature doesn't match the bytes (they or it were changed).</summary>
    Invalid,

    /// <summary>Made by another key than the pinned one.</summary>
    WrongKey,

    /// <summary>A signature this reader doesn't check (not v4, not a binary document signature, another algorithm).</summary>
    Unsupported,

    /// <summary>Not a readable signature.</summary>
    Malformed,
}

/// <summary>The verdict and, when not valid, why.</summary>
public sealed record OpenPgpVerification(OpenPgpVerdict Verdict, string? Detail = null)
{
    public bool IsValid => Verdict == OpenPgpVerdict.Valid;
}

/// <summary>
/// Checks an ASCII-armored detached OpenPGP signature (RFC 4880 §5.2.3–5.2.4) over a file's bytes with a pinned
/// <see cref="OpenPgpPublicKey"/>: version 4, signature type 0x00 (binary document), RSA PKCS#1 v1.5, SHA-256 / 384 / 512.
/// The issuer named in the signature (fingerprint or key ID subpacket) must be the pinned key; a critical subpacket this
/// reader doesn't know, or an expired signature, refuses it. Never throws for bad input.
/// </summary>
public static class OpenPgpSignature
{
    public static OpenPgpVerification Verify(ReadOnlySpan<byte> data, string armoredSignature, OpenPgpPublicKey key, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(armoredSignature);
        ArgumentNullException.ThrowIfNull(key);
        byte[] body;
        try
        {
            var packet = OpenPgpPackets.Read(OpenPgpArmor.Decode(armoredSignature, "SIGNATURE")).FirstOrDefault(p => p.Tag == 2);
            if (packet.Body is null)
            {
                return new OpenPgpVerification(OpenPgpVerdict.Malformed, "The file holds no signature packet.");
            }

            body = packet.Body;
        }
        catch (OpenPgpException e)
        {
            return new OpenPgpVerification(OpenPgpVerdict.Malformed, e.Message);
        }

        try
        {
            return Check(data, body, key, now ?? DateTimeOffset.UtcNow);
        }
        catch (OpenPgpException e)
        {
            return new OpenPgpVerification(OpenPgpVerdict.Malformed, e.Message);
        }
    }

    private static OpenPgpVerification Check(ReadOnlySpan<byte> data, byte[] body, OpenPgpPublicKey key, DateTimeOffset now)
    {
        if (body.Length < 6 || body[0] != 4)
        {
            return new OpenPgpVerification(OpenPgpVerdict.Unsupported, "Only version 4 signatures are checked.");
        }

        if (body[1] != 0x00)
        {
            return new OpenPgpVerification(OpenPgpVerdict.Unsupported, $"Signature type 0x{body[1]:x2} isn't a binary document signature.");
        }

        if (body[2] is not (1 or 3))
        {
            return new OpenPgpVerification(OpenPgpVerdict.Unsupported, $"Signature algorithm {body[2]} isn't RSA.");
        }

        HashAlgorithmName hash;
        switch (body[3])
        {
            case 8:
                hash = HashAlgorithmName.SHA256;
                break;
            case 9:
                hash = HashAlgorithmName.SHA384;
                break;
            case 10:
                hash = HashAlgorithmName.SHA512;
                break;
            default:
                return new OpenPgpVerification(OpenPgpVerdict.Unsupported, $"Hash algorithm {body[3]} isn't SHA-256, 384 or 512.");
        }

        var hashedLength = BinaryPrimitives.ReadUInt16BigEndian(Slice(body, 4, 2));
        var hashedEnd = 6 + hashedLength;
        var hashed = Subpackets(Slice(body, 6, hashedLength), hashedArea: true, out var hashedFacts);
        if (hashed is not null)
        {
            return hashed;
        }

        var unhashedLength = BinaryPrimitives.ReadUInt16BigEndian(Slice(body, hashedEnd, 2));
        Subpackets(Slice(body, hashedEnd + 2, unhashedLength), hashedArea: false, out var unhashedFacts);
        var position = hashedEnd + 2 + unhashedLength;
        var left16 = Slice(body, position, 2);
        position += 2;
        var signature = OpenPgpPackets.Mpi(body, ref position);

        var issuerFingerprint = hashedFacts.IssuerFingerprint ?? unhashedFacts.IssuerFingerprint;
        var issuerKeyId = hashedFacts.IssuerKeyId ?? unhashedFacts.IssuerKeyId;
        if (issuerFingerprint is not null && issuerFingerprint != key.Fingerprint)
        {
            return new OpenPgpVerification(OpenPgpVerdict.WrongKey, $"Signed by key {issuerFingerprint}, not {key.Fingerprint}.");
        }

        if (issuerKeyId is not null && issuerKeyId != key.KeyId)
        {
            return new OpenPgpVerification(OpenPgpVerdict.WrongKey, $"Signed by key {issuerKeyId}, not {key.KeyId}.");
        }

        if (hashedFacts.Created is { } created && hashedFacts.ExpiresAfter is { } expires && expires > 0
            && now > DateTimeOffset.FromUnixTimeSeconds(created + expires))
        {
            return new OpenPgpVerification(OpenPgpVerdict.Invalid, "The signature has expired.");
        }

        byte[] digest;
        using (var hasher = IncrementalHash.CreateHash(hash))
        {
            hasher.AppendData(data);
            hasher.AppendData(body, 0, hashedEnd);
            Span<byte> trailer = stackalloc byte[6];
            trailer[0] = 4;
            trailer[1] = 0xFF;
            BinaryPrimitives.WriteUInt32BigEndian(trailer[2..], (uint)hashedEnd);
            hasher.AppendData(trailer);
            digest = hasher.GetHashAndReset();
        }

        if (digest[0] != left16[0] || digest[1] != left16[1])
        {
            return new OpenPgpVerification(OpenPgpVerdict.Invalid, "The signature doesn't match the file.");
        }

        if (signature.Length > key.ModulusLength)
        {
            return new OpenPgpVerification(OpenPgpVerdict.Invalid, "The signature is longer than the key.");
        }

        var padded = new byte[key.ModulusLength];
        signature.CopyTo(padded, padded.Length - signature.Length);
        using var rsa = key.CreateRsa();
        return rsa.VerifyHash(digest, padded, hash, RSASignaturePadding.Pkcs1)
            ? new OpenPgpVerification(OpenPgpVerdict.Valid)
            : new OpenPgpVerification(OpenPgpVerdict.Invalid, "The signature doesn't match the file.");
    }

    private sealed class SubpacketFacts
    {
        public string? IssuerFingerprint { get; set; }

        public string? IssuerKeyId { get; set; }

        public long? Created { get; set; }

        public long? ExpiresAfter { get; set; }
    }

    /// <summary>Reads the subpackets this reader understands; a critical unknown one in the hashed area refuses the signature.</summary>
    private static OpenPgpVerification? Subpackets(byte[] area, bool hashedArea, out SubpacketFacts facts)
    {
        facts = new SubpacketFacts();
        var position = 0;
        while (position < area.Length)
        {
            int length = area[position++];
            if (length >= 192 && length < 255)
            {
                length = ((length - 192) << 8) + Slice(area, position, 1)[0] + 192;
                position++;
            }
            else if (length == 255)
            {
                length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(Slice(area, position, 4)));
                position += 4;
            }

            var subpacket = Slice(area, position, length);
            position += length;
            if (subpacket.Length == 0)
            {
                throw new OpenPgpException("An empty signature subpacket.");
            }

            var type = subpacket[0] & 0x7F;
            var critical = (subpacket[0] & 0x80) != 0;
            var value = subpacket.AsSpan(1);
            switch (type)
            {
                case 2 when value.Length == 4:
                    facts.Created = BinaryPrimitives.ReadUInt32BigEndian(value);
                    break;
                case 3 when value.Length == 4:
                    facts.ExpiresAfter = BinaryPrimitives.ReadUInt32BigEndian(value);
                    break;
                case 16 when value.Length == 8:
                    facts.IssuerKeyId = Convert.ToHexString(value);
                    break;
                case 33 when value.Length == 21 && value[0] == 4:
                    facts.IssuerFingerprint = Convert.ToHexString(value[1..]);
                    break;
                default:
                    if (critical && hashedArea)
                    {
                        return new OpenPgpVerification(OpenPgpVerdict.Unsupported, $"Critical signature subpacket {type} isn't understood.");
                    }

                    break;
            }
        }

        return null;
    }

    private static byte[] Slice(byte[] data, int start, int length)
    {
        if (start < 0 || length < 0 || start + length > data.Length)
        {
            throw new OpenPgpException("The signature packet is truncated.");
        }

        return data.AsSpan(start, length).ToArray();
    }
}

/// <summary>OpenPGP packet framing (RFC 4880 §4.2): old and new headers, no partial lengths.</summary>
internal static class OpenPgpPackets
{
    public static IEnumerable<(int Tag, byte[] Body)> Read(byte[] data)
    {
        var packets = new List<(int, byte[])>();
        var position = 0;
        while (position < data.Length)
        {
            var header = data[position++];
            if ((header & 0x80) == 0)
            {
                throw new OpenPgpException("Not an OpenPGP packet.");
            }

            int tag;
            long length;
            if ((header & 0x40) != 0)
            {
                tag = header & 0x3F;
                var first = Byte(data, position++);
                if (first < 192)
                {
                    length = first;
                }
                else if (first < 224)
                {
                    length = ((first - 192) << 8) + Byte(data, position++) + 192;
                }
                else if (first == 255)
                {
                    length = BinaryPrimitives.ReadUInt32BigEndian(Span(data, position, 4));
                    position += 4;
                }
                else
                {
                    throw new OpenPgpException("Partial-length packets aren't read.");
                }
            }
            else
            {
                tag = (header >> 2) & 0x0F;
                switch (header & 3)
                {
                    case 0:
                        length = Byte(data, position++);
                        break;
                    case 1:
                        length = BinaryPrimitives.ReadUInt16BigEndian(Span(data, position, 2));
                        position += 2;
                        break;
                    case 2:
                        length = BinaryPrimitives.ReadUInt32BigEndian(Span(data, position, 4));
                        position += 4;
                        break;
                    default:
                        length = data.Length - position;
                        break;
                }
            }

            if (length > data.Length - position)
            {
                throw new OpenPgpException("An OpenPGP packet is truncated.");
            }

            packets.Add((tag, Span(data, position, (int)length).ToArray()));
            position += (int)length;
        }

        return packets;
    }

    /// <summary>A multiprecision integer (bit count, then the big-endian bytes) without leading zero bytes.</summary>
    public static byte[] Mpi(byte[] data, ref int position)
    {
        var bits = BinaryPrimitives.ReadUInt16BigEndian(Span(data, position, 2));
        position += 2;
        var length = (bits + 7) / 8;
        var value = Span(data, position, length).ToArray();
        position += length;
        var zeros = 0;
        while (zeros < value.Length - 1 && value[zeros] == 0)
        {
            zeros++;
        }

        if (value.Length == 0)
        {
            throw new OpenPgpException("An empty number.");
        }

        return zeros == 0 ? value : value[zeros..];
    }

    private static byte Byte(byte[] data, int position) =>
        position < data.Length ? data[position] : throw new OpenPgpException("An OpenPGP packet is truncated.");

    private static ReadOnlySpan<byte> Span(byte[] data, int start, int length) =>
        start >= 0 && length >= 0 && start + length <= data.Length
            ? data.AsSpan(start, length)
            : throw new OpenPgpException("An OpenPGP packet is truncated.");
}

/// <summary>ASCII armor (RFC 4880 §6.2): base64 between the BEGIN / END lines, headers skipped, the CRC-24 checked when present.</summary>
internal static class OpenPgpArmor
{
    public static byte[] Decode(string text, string kind)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).ToList();
        var begin = lines.IndexOf($"-----BEGIN PGP {kind}-----");
        if (begin < 0)
        {
            throw new OpenPgpException($"No \"BEGIN PGP {kind}\" line.");
        }

        var index = begin + 1;
        if (index < lines.Count && lines[index].Contains(':', StringComparison.Ordinal))
        {
            while (index < lines.Count && lines[index].Length > 0)
            {
                index++;
            }
        }

        var base64 = new StringBuilder();
        string? checksum = null;
        var ended = false;
        for (; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.StartsWith("-----END PGP ", StringComparison.Ordinal))
            {
                ended = true;
                break;
            }

            if (line.StartsWith('=') && line.Length == 5)
            {
                checksum = line[1..];
                continue;
            }

            base64.Append(line);
        }

        if (!ended)
        {
            throw new OpenPgpException($"No \"END PGP {kind}\" line.");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException)
        {
            throw new OpenPgpException("The armored text isn't base64.");
        }

        if (checksum is not null)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(checksum);
            }
            catch (FormatException)
            {
                throw new OpenPgpException("The armor checksum isn't base64.");
            }

            var crc = Crc24(data);
            if (expected.Length != 3 || expected[0] != (byte)(crc >> 16) || expected[1] != (byte)(crc >> 8) || expected[2] != (byte)crc)
            {
                throw new OpenPgpException("The armor checksum doesn't match.");
            }
        }

        return data;
    }

    private static int Crc24(byte[] data)
    {
        var crc = 0xB704CE;
        foreach (var b in data)
        {
            crc ^= b << 16;
            for (var i = 0; i < 8; i++)
            {
                crc <<= 1;
                if ((crc & 0x1000000) != 0)
                {
                    crc ^= 0x1864CFB;
                }
            }
        }

        return crc & 0xFFFFFF;
    }
}
