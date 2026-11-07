using System.IO.Compression;

namespace AiLocalPack.TestKit;

/// <summary>
/// Synthetic binary patches for install tests: a minimal VCDIFF encoder (one window: COPY of the prefix the two files
/// share from the source, ADD of the rest, with xdelta3's Adler-32) and the patch archive layout (a ZIP with one
/// <c>&lt;path&gt;.vcdiff</c> entry per file). The decoder itself is tested against real xdelta3 output
/// (<c>tests/fixtures/vcdiff</c>).
/// </summary>
public static class VcdiffFixture
{
    public static byte[] Encode(byte[] source, byte[] target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var prefix = 0;
        while (prefix < source.Length && prefix < target.Length && source[prefix] == target[prefix])
        {
            prefix++;
        }

        var instructions = new List<byte>();
        var addresses = new List<byte>();
        if (prefix > 0)
        {
            instructions.Add(19); // COPY, size follows, mode 0 (absolute address)
            instructions.AddRange(Varint(prefix));
            addresses.AddRange(Varint(0));
        }

        var added = target[prefix..];
        if (added.Length > 0)
        {
            instructions.Add(1); // ADD, size follows
            instructions.AddRange(Varint(added.Length));
        }

        byte[] encoding =
        [
            .. Varint(target.Length), 0, .. Varint(added.Length), .. Varint(instructions.Count), .. Varint(addresses.Count),
            .. Adler32(target), .. added, .. instructions, .. addresses,
        ];
        var hasSource = source.Length > 0;
        byte[] window =
        [
            (byte)((hasSource ? 0x01 : 0x00) | 0x04),
            .. hasSource ? [.. Varint(source.Length), .. Varint(0)] : Array.Empty<byte>(),
            .. Varint(encoding.Length),
            .. encoding,
        ];
        return [0xD6, 0xC3, 0xC4, 0x00, 0x00, .. window];
    }

    /// <summary>Writes the patch archive: <paramref name="deltas"/> = game-relative path → delta.</summary>
    public static void Archive(string path, IReadOnlyDictionary<string, byte[]> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (file, delta) in deltas)
        {
            using var entry = zip.CreateEntry(file + ".vcdiff").Open();
            entry.Write(delta);
        }
    }

    private static byte[] Varint(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        for (value >>= 7; value > 0; value >>= 7)
        {
            bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
        }

        return [.. bytes];
    }

    private static byte[] Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var sum = (b << 16) | a;
        return [(byte)(sum >> 24), (byte)(sum >> 16), (byte)(sum >> 8), (byte)sum];
    }
}
