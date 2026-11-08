using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Agents.Install;

namespace AiLocalPack.Agents.Tests.Install;

/// <summary>
/// A throwaway OpenPGP v4 RSA key that signs like a vendor's release key (detached, binary document, SHA-512), so tests
/// can sign their own manifests.
/// </summary>
internal sealed class TestPgp : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa = RSA.Create(2048);

    public TestPgp()
    {
        var parameters = _rsa.ExportParameters(false);
        var body = new List<byte> { 4 };
        body.AddRange(BigEndian((uint)Created.ToUnixTimeSeconds()));
        body.Add(1);
        body.AddRange(Mpi(parameters.Modulus!));
        body.AddRange(Mpi(parameters.Exponent!));
        KeyPacket = [.. body];
        ArmoredPublicKey = Armor("PUBLIC KEY BLOCK", Packet(6, KeyPacket));
        PublicKey = OpenPgpPublicKey.Read(ArmoredPublicKey);
    }

    public byte[] KeyPacket { get; }

    public string ArmoredPublicKey { get; }

    public OpenPgpPublicKey PublicKey { get; }

    /// <summary>An armored detached signature of <paramref name="data"/>.</summary>
    /// <param name="signatureType">0x00 = binary document.</param>
    /// <param name="issuerFingerprint">The issuer named (default this key's).</param>
    /// <param name="extraHashedSubpacket">One more hashed subpacket (type byte first).</param>
    public string Sign(byte[] data, byte signatureType = 0x00, string? issuerFingerprint = null, byte[]? extraHashedSubpacket = null, uint? expiresAfter = null)
    {
        var fingerprint = Convert.FromHexString(issuerFingerprint ?? PublicKey.Fingerprint);
        var hashedArea = new List<byte>();
        hashedArea.AddRange(Subpacket([33, 4, .. fingerprint]));
        hashedArea.AddRange(Subpacket([2, .. BigEndian((uint)Created.ToUnixTimeSeconds())]));
        if (expiresAfter is { } expires)
        {
            hashedArea.AddRange(Subpacket([3, .. BigEndian(expires)]));
        }

        if (extraHashedSubpacket is not null)
        {
            hashedArea.AddRange(Subpacket(extraHashedSubpacket));
        }

        var head = new List<byte> { 4, signatureType, 1, 10 };
        head.AddRange(BigEndian16((ushort)hashedArea.Count));
        head.AddRange(hashedArea);
        var trailer = new List<byte> { 4, 0xFF };
        trailer.AddRange(BigEndian((uint)head.Count));
        var digest = SHA512.HashData([.. data, .. head, .. trailer]);
        var signature = _rsa.SignHash(digest, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        var unhashed = Subpacket([16, .. fingerprint[^8..]]);
        var body = new List<byte>(head);
        body.AddRange(BigEndian16((ushort)unhashed.Length));
        body.AddRange(unhashed);
        body.Add(digest[0]);
        body.Add(digest[1]);
        body.AddRange(Mpi(signature));
        return Armor("SIGNATURE", Packet(2, [.. body]));
    }

    public string Sign(string text) => Sign(Encoding.UTF8.GetBytes(text));

    public void Dispose() => _rsa.Dispose();

    private static byte[] Subpacket(byte[] content) => [(byte)content.Length, .. content];

    private static byte[] Packet(int tag, byte[] body)
    {
        var header = new List<byte> { (byte)(0xC0 | tag) };
        if (body.Length < 192)
        {
            header.Add((byte)body.Length);
        }
        else
        {
            header.Add(0xFF);
            header.AddRange(BigEndian((uint)body.Length));
        }

        return [.. header, .. body];
    }

    private static byte[] Mpi(byte[] value)
    {
        var start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }

        var trimmed = value[start..];
        var bits = (trimmed.Length - 1) * 8 + (32 - BitOperations(trimmed[0]));
        return [.. BigEndian16((ushort)bits), .. trimmed];
    }

    private static int BitOperations(byte b) => System.Numerics.BitOperations.LeadingZeroCount((uint)b);

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] BigEndian16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static string Armor(string kind, byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var text = new StringBuilder($"-----BEGIN PGP {kind}-----\n\n");
        for (var i = 0; i < base64.Length; i += 64)
        {
            text.Append(base64.AsSpan(i, Math.Min(64, base64.Length - i))).Append('\n');
        }

        var crc = Crc24(data);
        text.Append('=').Append(Convert.ToBase64String([(byte)(crc >> 16), (byte)(crc >> 8), (byte)crc])).Append('\n');
        text.Append($"-----END PGP {kind}-----\n");
        return text.ToString();
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
