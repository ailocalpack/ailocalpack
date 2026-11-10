using System.Buffers.Binary;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Agent;

/// <summary>
/// A synthetic game in a made-up format no toolkit tool reads (task 57, <c>dev fixture custom &lt;dir&gt;</c>): checks
/// the agent writing its own extractor and rebuilder and the binary patch pack. "Lantern Tale":
/// <list type="bullet">
/// <item><c>data/text.ltx</c>: <c>LTX1</c>, u16 LE count, then per entry u16 LE id, u8 kind (0 dialogue, 1 menu,
/// 2 item), u16 LE byte length, the UTF-8 text (<c>{hero}</c> and <c>^C1</c>…<c>^C0</c> colour codes in some), and at the end a
/// u32 LE sum of every text byte (the game refuses a file whose sum doesn't match).</item>
/// <item><c>data/names.ltx</c>: the same format, the speakers' names.</item>
/// <item><c>LanternTale.exe</c> (a stub), <c>assets/logo.bin</c> (bytes no text is in), <c>config.ini</c>.</item>
/// </list>
/// </summary>
public static class CustomFormatFixture
{
    public static IReadOnlyList<(ushort Id, byte Kind, string Text)> Texts { get; } =
    [
        (1, 1, "New Game"),
        (2, 1, "Continue"),
        (3, 1, "Options"),
        (4, 1, "Quit"),
        (10, 0, "The lantern flickers. Someone is waiting at the old bridge."),
        (11, 0, "{hero}, you came! I was afraid the fog would keep you away."),
        (12, 0, "Take this ^C1Ember Stone^C0. It glows when danger is near."),
        (13, 0, "Follow the river north until you reach the mill."),
        (20, 2, "Ember Stone"),
        (21, 2, "A warm stone that glows near danger."),
        (22, 2, "Lantern Oil"),
        (23, 2, "Keeps the lantern burning for one night."),
    ];

    public static IReadOnlyList<(ushort Id, byte Kind, string Text)> Names { get; } =
    [
        (1, 0, "Mira"),
        (2, 0, "Old Tobin"),
    ];

    /// <summary>Writes the game into <paramref name="dir"/>; returns the files written (relative).</summary>
    public static IReadOnlyList<string> Create(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        var files = new List<string>();
        void Write(string relative, byte[] content)
        {
            var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            files.Add(relative);
        }

        Write("data/text.ltx", Encode(Texts));
        Write("data/names.ltx", Encode(Names));
        Write("LanternTale.exe", [(byte)'M', (byte)'Z', .. new byte[126]]);
        var logo = new byte[2048];
        new Random(57).NextBytes(logo);
        Write("assets/logo.bin", logo);
        Write("config.ini", Encoding.UTF8.GetBytes("[window]\nwidth=1280\nheight=720\n"));
        return files;
    }

    /// <summary>The format's bytes for <paramref name="entries"/>.</summary>
    public static byte[] Encode(IEnumerable<(ushort Id, byte Kind, string Text)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        using var stream = new MemoryStream();
        stream.Write("LTX1"u8);
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)list.Count);
        stream.Write(u16);
        uint sum = 0;
        foreach (var (id, kind, text) in list)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            BinaryPrimitives.WriteUInt16LittleEndian(u16, id);
            stream.Write(u16);
            stream.WriteByte(kind);
            BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)bytes.Length);
            stream.Write(u16);
            stream.Write(bytes);
            foreach (var b in bytes)
            {
                sum += b;
            }
        }

        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, sum);
        stream.Write(u32);
        return stream.ToArray();
    }

    /// <summary>Reads the format back (tests check a rebuilt file with it).</summary>
    /// <exception cref="InvalidDataException">Not the format, or the sum doesn't match.</exception>
    public static IReadOnlyList<(ushort Id, byte Kind, string Text)> Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 10 || !data.AsSpan(0, 4).SequenceEqual("LTX1"u8))
        {
            throw new InvalidDataException("Not an LTX1 file.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        var at = 6;
        uint sum = 0;
        var list = new List<(ushort, byte, string)>();
        for (var i = 0; i < count; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            var kind = data[at + 2];
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 3));
            var bytes = data.AsSpan(at + 5, length);
            foreach (var b in bytes)
            {
                sum += b;
            }

            list.Add((id, kind, Encoding.UTF8.GetString(bytes)));
            at += 5 + length;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) == sum ? list : throw new InvalidDataException("The checksum doesn't match.");
    }
}
