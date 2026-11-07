using System.Text;
using AiLocalPack.Toolkit.Fonts;

namespace AiLocalPack.Toolkit.Recipes.RpgMaker;

/// <summary>
/// A tiny synthetic RPG Maker MV / MZ game (no real game files; the executable is a text stub) for tests and manual
/// checks (<c>ailocalpack dev fixture rpgmaker</c>). Every kind of text the <c>rpgmaker</c> tool reads is in it, with
/// escape codes, non-ASCII text, and things it must leave alone (plugin command, script, note, comment, editor names).
/// Data files are written the way the editors write them: one line per database entry. The text font is a synthetic
/// Latin-only font (<see cref="FontWriter"/>: ASCII and Latin-1, family <see cref="FontFamily"/>): MZ names it in
/// <c>System.json</c> (<c>advanced.mainFontFilename</c>, WOFF as MZ ships its fonts), MV in <c>fonts/gamefont.css</c>.
/// </summary>
public static class RpgMakerFixture
{
    /// <summary>Writes the game into <paramref name="dir"/> (created if missing; existing files of the same name are replaced).</summary>
    /// <param name="mv">MV layout (<c>www/</c>, <c>rpg_core.js</c>) instead of MZ.</param>
    /// <returns>Paths of the written files, relative to <paramref name="dir"/>.</returns>
    public static IReadOnlyList<string> Create(string dir, bool mv = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        var project = mv ? "www/" : string.Empty;
        var latin = FontWriter.TrueType(FontFamily, [.. FontWriter.Ascii, .. Enumerable.Range(0xA0, 0x60)]);
        var binary = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (mv)
        {
            binary["www/fonts/fixture-sans.ttf"] = latin;
            binary["www/fonts/gamefont.css"] = Encoding.UTF8.GetBytes(MvFontCss);
        }
        else
        {
            binary[$"fonts/{MainFont}"] = FontWriter.Woff(latin);
            binary["fonts/fixture-numbers.woff"] = FontWriter.Woff(FontWriter.TrueType("Fixture Numbers", Enumerable.Range('0', 10)));
        }

        var files = new Dictionary<string, string>
        {
            ["Game.exe"] = "MZ stub: not a real executable",
            [mv ? "www/js/rpg_core.js" : "js/rmmz_core.js"] = "// RPG Maker core (stub)\n",
            [$"{project}js/plugins.js"] = "var $plugins =\n[\n{\"name\":\"TextPop\",\"status\":true,\"parameters\":{\"Greeting\":\"Hello from a plugin\"}}\n];\n",
            [$"{project}data/System.json"] = mv ? System : System.Replace("\"airship\":", MzAdvanced, StringComparison.Ordinal),
            [$"{project}data/Actors.json"] = Actors,
            [$"{project}data/Classes.json"] = "[\nnull,\n{\"id\":1,\"name\":\"Swordsman\",\"note\":\"\"}\n]",
            [$"{project}data/Items.json"] = Items,
            [$"{project}data/Weapons.json"] = "[\nnull,\n{\"id\":1,\"name\":\"Bronze Sword\",\"description\":\"A plain blade.\",\"price\":100,\"note\":\"\"}\n]",
            [$"{project}data/Armors.json"] = "[\nnull,\n{\"id\":1,\"name\":\"Leather Cap\",\"description\":\"\",\"price\":30,\"note\":\"\"}\n]",
            [$"{project}data/Skills.json"] = Skills,
            [$"{project}data/States.json"] = States,
            [$"{project}data/Enemies.json"] = "[\nnull,\n{\"id\":1,\"name\":\"Slime\",\"exp\":3,\"gold\":5,\"note\":\"\"}\n]",
            [$"{project}data/Troops.json"] = Troops,
            [$"{project}data/CommonEvents.json"] = CommonEvents,
            [$"{project}data/MapInfos.json"] = "[\nnull,\n{\"id\":1,\"expanded\":false,\"name\":\"VILLAGE (editor name)\",\"order\":1,\"parentId\":0}\n]",
            [$"{project}data/Map001.json"] = Map,
        };

        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        foreach (var (path, content) in binary)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        return [.. files.Keys.Concat(binary.Keys).Order(StringComparer.Ordinal)];
    }

    /// <summary>Family name of the fixture's text font (Latin-only).</summary>
    public const string FontFamily = "Fixture Sans";

    /// <summary>MZ: the text font's file in <c>fonts/</c>, as <c>System.json</c> names it.</summary>
    public const string MainFont = "fixture-sans.woff";

    private const string MzAdvanced = """
        "advanced":{"gameId":12345678,"screenWidth":816,"screenHeight":624,"uiAreaWidth":816,"uiAreaHeight":624,"numberFontFilename":"fixture-numbers.woff","fallbackFonts":"Verdana, sans-serif","fontSize":26,"mainFontFilename":"fixture-sans.woff","windowOpacity":192},"airship":
        """;

    private const string MvFontCss = """
        @font-face {
            font-family: GameFont;
            src: url("fixture-sans.ttf");
        }

        """;

    private const string System = """
        {"airship":{"bgm":{"name":"Ship3","pan":0,"pitch":100,"volume":90},"characterIndex":3},"armorTypes":["","General Armor","Magic Armor"],"currencyUnit":"G","elements":["","Physical","Fire"],"equipTypes":["","Weapon","Shield","Head"],"gameTitle":"Fixture Quest","locale":"en_US","skillTypes":["","Magic","Special"],"terms":{"basic":["Level","Lv","HP","HP"],"commands":["Fight","Escape","Attack","Guard","Item","Skill",null,"New Game",""],"params":["Max HP","Max MP"],"messages":{"actionFailure":"There was no effect on %1!","obtainGold":"%1\\G found!","possession":"Possession"}},"versionId":12345678,"weaponTypes":["","Dagger","Sword"]}
        """;

    private const string Actors = """
        [
        null,
        {"id":1,"battlerName":"Actor1_1","name":"Harold","nickname":"The Brave","note":"<Plugin: keep me>","profile":"A young knight\nfrom the north.","initialLevel":1},
        {"id":2,"battlerName":"","name":"","nickname":"","note":"","profile":"","initialLevel":1}
        ]
        """;

    private const string Items = """
        [
        null,
        {"id":1,"name":"Potion","description":"Restores 500 HP to one ally.","price":50,"note":""},
        {"id":2,"name":"Café au lait","description":"A warm drink. \\C[2]Rare!\\C[0]","price":5,"note":"<Category: Drink>"}
        ]
        """;

    private const string Skills = """
        [
        null,
        {"id":1,"name":"Attack","description":"","message1":" attacks!","message2":"","note":""},
        {"id":2,"name":"Fire","description":"Deals fire damage to one enemy.","message1":" casts %1!","message2":"","note":""}
        ]
        """;

    private const string States = """
        [
        null,
        {"id":1,"name":"Knockout","message1":" has fallen!","message2":" is slain!","message3":"","message4":" revives!","note":""}
        ]
        """;

    private const string Troops = """
        [
        null,
        {"id":1,"members":[{"enemyId":1,"x":400,"y":300,"hidden":false}],"name":"Slime*2","pages":[{"conditions":{"turnEnding":false},"list":[{"code":101,"indent":0,"parameters":["",0,0,2]},{"code":401,"indent":0,"parameters":["The slime wobbles menacingly!"]},{"code":0,"indent":0,"parameters":[]}],"span":0}]}
        ]
        """;

    private const string CommonEvents = """
        [
        null,
        {"id":1,"list":[{"code":108,"indent":0,"parameters":["Comment: not shown in game"]},{"code":355,"indent":0,"parameters":["$gameMessage.add('script text');"]},{"code":356,"indent":0,"parameters":["ShowPopup Hello"]},{"code":105,"indent":0,"parameters":[2,false]},{"code":405,"indent":0,"parameters":["Long ago, the kingdom of \\C[1]Ash\\C[0] fell."]},{"code":405,"indent":0,"parameters":["Only \\N[1] remembers."]},{"code":320,"indent":0,"parameters":[1,"Harry"]},{"code":0,"indent":0,"parameters":[]}],"name":"Intro (editor name)","switchId":1,"trigger":0}
        ]
        """;

    private const string Map = """
        {
        "autoplayBgm":false,"displayName":"Village of Ash","note":"",
        "events":[
        null,
        {"id":1,"name":"Elder","note":"","pages":[{"conditions":{"actorValid":false},"list":[{"code":101,"indent":0,"parameters":["Actor1",0,0,2,"Elder"]},{"code":401,"indent":0,"parameters":["Welcome, \\N[1]. You have \\V[3] coins."]},{"code":401,"indent":0,"parameters":["The road north is closed.\\|"]},{"code":102,"indent":0,"parameters":[["Ask about the road","Leave"],1,0,2,0]},{"code":402,"indent":0,"parameters":[0,"Ask about the road"]},{"code":401,"indent":1,"parameters":["Wolves, \\C[2]many wolves\\C[0]."]},{"code":0,"indent":1,"parameters":[]},{"code":402,"indent":0,"parameters":[1,"Leave"]},{"code":0,"indent":1,"parameters":[]},{"code":404,"indent":0,"parameters":[]},{"code":0,"indent":0,"parameters":[]}]}],"x":5,"y":7},
        {"id":2,"name":"Sign","note":"","pages":[{"list":[{"code":401,"indent":0,"parameters":["North: closed."]},{"code":0,"indent":0,"parameters":[]}]}],"x":8,"y":2}
        ],
        "height":13,"width":17
        }
        """;
}
