using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>A synthetic UE game: a standard pak, native English and French locres, locmeta. No Unreal/game files.</summary>
public static class UnrealFixture
{
    public const string Paks = "Sample/Content/Paks";
    public const string Pak = Paks + "/pakchunk0.pak";
    public const string English = "Sample/Content/Localization/Game/en/Game.locres";
    public const string French = "Sample/Content/Localization/Game/fr/Game.locres";
    public const string Meta = "Sample/Content/Localization/Game/Game.locmeta";

    public static IReadOnlyList<string> Create(string root, byte version = 3, bool macBundle = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var prefix = macBundle ? "Sample.app/Contents/UE4/" : "";
        var files = new Dictionary<string, byte[]>
        {
            [English] = Resource(version).Write(),
            [French] = Resource(version, french: true).Write(),
            [Meta] = Metadata(),
            ["Sample/Content/Opaque.bin"] = [1, 2, 3, 4],
        };
        var path = prefix + Pak;
        Directory.CreateDirectory(Path.Combine(root, prefix + Paks));
        Directory.CreateDirectory(Path.Combine(root, prefix + "Engine"));
        File.WriteAllBytes(Path.Combine(root, path), UnrealPakWriter.Write(files));
        return [path];
    }

    internal static UnrealLocres Resource(byte version, bool french = false) => new(version,
    [
        new("Menu", "Play", 0x1234, 0x5678, 0x11223344, french ? "Jouer" : "Play"),
        new("Dialogue/:", "Greeting/%", 0x8765, 0x4321, 0x55667788, french ? "Bonjour {Player}!" : "Hello {Player}!"),
        new("Menu", "Hint", 0x1234, 0x12345678, 0xABCDEF01, "<b>Fuel</>: {0} / 100 🚀"),
    ]);

    internal static byte[] Metadata(byte version = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Convert.FromHexString("4FEE4CA1684855836C4C46BD70DA507C"));
        writer.Write(version);
        UnrealLocres.WriteString(writer, "en");
        UnrealLocres.WriteString(writer, "en/Game.locres");
        if (version >= 1)
        {
            writer.Write(2);
            UnrealLocres.WriteString(writer, "en");
            UnrealLocres.WriteString(writer, "fr");
        }

        if (version >= 2) writer.Write(0); // UE boolean is int32.
        {
            return stream.ToArray();
        }
    }
}
