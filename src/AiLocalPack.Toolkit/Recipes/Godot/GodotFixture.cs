using System.Security.Cryptography;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Godot;

/// <summary>
/// A tiny synthetic Godot game (no real game files; the executable is a text stub) for tests and manual checks
/// (<c>ailocalpack dev fixture godot</c>): <c>Game.exe</c> and its <c>Game.pck</c> (Godot 3: pack format 1, Godot 4:
/// format 2), or the pack embedded in <c>Game.exe</c>. Translated (the default): a compressed English translation
/// whose keys are named by a scene, a resource and a compiled script (one key by none, so it stays a hash), a plain
/// English translation, a French one the toolkit leaves alone; texts with BBCode, format fields and non-ASCII letters.
/// Untranslated: the scenes hold the texts themselves. Plus an opaque asset copied as is.
/// </summary>
public static class GodotFixture
{
    /// <summary>The pack, relative to the game folder (or the executable, when embedded).</summary>
    public const string PackFile = "Game.pck";

    public const string ExecutableFile = "Game.exe";

    /// <summary>Path of the compressed English translation in the pack.</summary>
    public const string EnglishTranslation = "localization/strings.en.translation";

    /// <summary>Path of the plain English translation in the pack.</summary>
    public const string ExtraTranslation = "localization/extra.en.translation";

    public const string FrenchTranslation = "localization/strings.fr.translation";

    public const string MenuScene = "ui/menu.tscn";

    /// <summary>Text of the message whose key no file of the game names.</summary>
    public const string UnnamedText = "The end?";

    /// <summary>The compressed English translation's messages.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> English { get; } =
    [
        new("MENU_PLAY", "Play"),
        new("MENU_QUIT", "Quit the game"),
        new("HUD_GOLD", "Gold: {0}"),
        new("ITEM_SWORD", "Bronze Sword"),
        new("ITEM_SWORD_DESC", "A plain blade. Deals [color=red]%d[/color] damage to the enemy."),
        new("SHOP_CAFE", "Café of the Wanderer"),
        new("SECRET_ENDING", UnnamedText),
    ];

    /// <summary>The plain English translation's messages.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Extra { get; } = [new("CREDITS_TITLE", "Credits")];

    /// <summary>Writes the game into <paramref name="dir"/> (created if missing; files of the same name are replaced).</summary>
    /// <param name="major">Godot 3 (pack format 1, resource format 3) or 4 (pack format 2, resource format 5).</param>
    /// <param name="embedded">The pack at the end of <c>Game.exe</c> instead of <c>Game.pck</c>.</param>
    /// <param name="translated">With translation resources; without, the scenes hold the texts.</param>
    /// <returns>Paths of the written files, relative to <paramref name="dir"/>.</returns>
    public static IReadOnlyList<string> Create(string dir, uint major = 3, bool embedded = false, bool translated = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        Directory.CreateDirectory(dir);
        var stub = Encoding.ASCII.GetBytes("MZ stub: not a real executable\n");
        var pack = Pack(major == 3 ? 1u : 2u, major, [.. Files(major, translated)], start: embedded ? stub.Length : 0);
        if (embedded)
        {
            using var output = new MemoryStream();
            output.Write(stub);
            output.Write(pack);
            output.Write(BitConverter.GetBytes((ulong)pack.Length));
            output.Write("GDPC"u8);
            File.WriteAllBytes(Path.Combine(dir, ExecutableFile), output.ToArray());
            return [ExecutableFile];
        }

        File.WriteAllBytes(Path.Combine(dir, ExecutableFile), stub);
        File.WriteAllBytes(Path.Combine(dir, PackFile), pack);
        return [ExecutableFile, PackFile];
    }

    /// <summary>
    /// A pack of <paramref name="files"/> (path without <c>res://</c>, content), as Godot's exporter writes one: format
    /// 1 (paths with <c>res://</c>, zero-padded to 4 bytes, offsets from the start of the file), 2 (with
    /// <paramref name="flags"/>; <c>2</c> = file base from the pack's start) or 3 (directory after the files, paths
    /// without <c>res://</c>). <paramref name="start"/> = where the pack will sit in its file (an embedded pack).
    /// </summary>
    /// <param name="encrypted">Files flagged as encrypted (their bytes are stored as given).</param>
    public static byte[] Pack(uint format, uint major, IReadOnlyList<(string Path, byte[] Content)> files, long start = 0, uint flags = 2, IReadOnlySet<string>? encrypted = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var names = files.Select(f =>
        {
            var name = Encoding.UTF8.GetBytes(format == 3 ? f.Path : "res://" + f.Path);
            return name.Concat(new byte[(4 - (name.Length % 4)) % 4]).ToArray();
        }).ToArray();
        var headerSize = format switch { 1 => 84, 2 => 96, _ => 104 };
        var directorySize = 4 + names.Sum(n => 4 + n.Length + 32 + (format >= 2 ? 4 : 0));
        long Align(long v) => (v + 15) / 16 * 16;
        var fileBase = Align(format == 3 ? headerSize : headerSize + directorySize);
        var offsets = new long[files.Count];
        var at = fileBase;
        for (var i = 0; i < files.Count; i++)
        {
            offsets[i] = at;
            at = Align(at + files[i].Content.Length);
        }

        using var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(GodotPack.Magic);
        writer.Write(format);
        writer.Write(major);
        writer.Write(major == 3 ? 5u : 2u);
        writer.Write(1u);
        if (format >= 2)
        {
            writer.Write(flags);
            writer.Write((ulong)(format == 3 || (flags & 2) != 0 ? fileBase : start + fileBase));
        }

        if (format == 3)
        {
            writer.Write((ulong)at);
        }

        writer.Write(new byte[64]);
        void Directory()
        {
            writer.Write((uint)files.Count);
            for (var i = 0; i < files.Count; i++)
            {
                writer.Write((uint)names[i].Length);
                writer.Write(names[i]);
                writer.Write((ulong)(format == 1 ? start + offsets[i] : offsets[i] - fileBase));
                writer.Write((ulong)files[i].Content.Length);
                writer.Write(MD5.HashData(files[i].Content));
                if (format >= 2)
                {
                    writer.Write(encrypted?.Contains(files[i].Path) == true ? 1u : 0u);
                }
            }
        }

        if (format != 3)
        {
            Directory();
        }

        for (var i = 0; i < files.Count; i++)
        {
            output.SetLength(offsets[i]);
            output.Position = offsets[i];
            writer.Write(files[i].Content);
        }

        output.SetLength(at);
        output.Position = at;
        if (format == 3)
        {
            Directory();
        }

        writer.Flush();
        return output.ToArray();
    }

    private static IEnumerable<(string Path, byte[] Content)> Files(uint major, bool translated)
    {
        yield return ("project.binary", [.. "ECFG"u8, 0, 0, 0, 0]);
        yield return (MenuScene, Encoding.UTF8.GetBytes(Menu(major, translated)));
        yield return ("items/sword.tres", Encoding.UTF8.GetBytes(
            $"[gd_resource type=\"Resource\" load_steps=2 format={(major == 3 ? 2 : 3)}]\n\n[resource]\nname = \"ITEM_SWORD\"\ndescription = \"ITEM_SWORD_DESC\"\nprice = 100\n"));

        // A compiled script: binary noise around its string constants (the key and a format string).
        yield return ("scripts/hud.gdc", [.. "GDSC"u8, 13, 0, 0, 0, 0x8C, 0xB6, 0x01, 4, 0, 0, 0, 8, 0, 0, 0, .. "HUD_GOLD"u8, 0, 0, 0, 0, 5, 0, 0, 0, .. "SHOP_CAFE"u8, 0, 0, 0, 0xF0, 0x9F]);
        yield return (".import/icon.png-1f2e3d4c.stex", [.. Enumerable.Range(0, 3000).Select(i => (byte)((i * 37) ^ (i >> 3)))]);
        if (translated)
        {
            yield return (ExtraTranslation, GodotTranslation.Create(major, "en", Extra, hashed: false));
            yield return (FrenchTranslation, GodotTranslation.Create(major, "fr", [.. English.Select(m => new KeyValuePair<string, string>(m.Key, "fr: " + m.Value))], hashed: true));
            yield return (EnglishTranslation, GodotTranslation.Create(major, "en", English, hashed: true));
        }
    }

    private static string Menu(uint major, bool translated)
    {
        var (play, quit, gold, tooltip) = translated
            ? ("MENU_PLAY", "MENU_QUIT", "HUD_GOLD", "CREDITS_TITLE")
            : ("Play", "Quit \"now\"\\later", "[color=yellow]Gold[/color]: %d\nper wave", "Opens the credits");
        var tooltipProperty = major == 3 ? "hint_tooltip" : "tooltip_text";
        var richProperty = major == 3 ? "bbcode_text" : "text";
        return $$"""
            [gd_scene load_steps=2 format={{(major == 3 ? 2 : 3)}}]

            [ext_resource path="res://ui/theme.tres" type="Theme" id=1]

            [node name="Menu" type="Control"]
            anchor_right = 1.0
            theme = ExtResource( 1 )

            [node name="Play" type="Button" parent="."]
            text = "{{GodotTextScene.Quote(play)[1..^1]}}"
            {{tooltipProperty}} = "{{tooltip}}"

            [node name="Quit" type="Button" parent="."]
            margin_top = 40.0
            text = "{{GodotTextScene.Quote(quit)[1..^1]}}"

            [node name="Gold" type="RichTextLabel" parent="Play"]
            {{richProperty}} = "{{GodotTextScene.Quote(gold)[1..^1]}}"
            meta = {
            "note": "text = \"not a property\""
            }

            [node name="Version" type="Label" parent="."]
            text = "1.0.3"

            [connection signal="pressed" from="Play" to="." method="_on_play"]

            """;
    }
}
