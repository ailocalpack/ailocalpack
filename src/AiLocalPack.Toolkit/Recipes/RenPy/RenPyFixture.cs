using System.Text;
using AiLocalPack.Toolkit.Fonts;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>
/// A tiny synthetic Ren'Py game (no engine, stub launcher) for tests and manual checks (<c>ailocalpack dev fixture
/// renpy</c>): compiled scripts written the way Ren'Py 7 writes them (<c>RENPY RPC2</c>, slot 1, protocol 2 pickles of
/// <c>renpy.ast</c> nodes), loose under <c>game/</c> or packed into <c>game/scripts.rpa</c>, plus the matching
/// <c>.rpy</c> sources for reading. Its lines include ones from Ren'Py's sample game <i>The Question</i>, so their
/// identifiers are the ones Ren'Py's own <c>translate</c> output gives (<see cref="KnownIdentifiers"/>), and every case
/// the tool handles: speakers with attributes, <c>with</c>, <c>nointeract</c>, a repeated line (<c>_1</c> suffix), a
/// <c>voice</c> statement in the block, text tags and interpolation, menus, <c>if</c> branches, a hidden label,
/// <c>_()</c> texts in Python and a screen, and things to leave alone (other statements, an existing translation).
/// Fonts: <see cref="TextFont"/> (synthetic, Latin-only: ASCII and Latin-1; inside the archive when archived) and
/// Ren'Py's own <c>renpy/common/DejaVuSans.ttf</c> (synthetic too, with Latin, Greek and Cyrillic like the real one).
/// </summary>
public static class RenPyFixture
{
    /// <summary>Lines of the fixture with the identifiers Ren'Py gives them (<c>the_question/game/tl/*/script.rpy</c> of Ren'Py).</summary>
    public static IReadOnlyDictionary<string, string> KnownIdentifiers { get; } = new Dictionary<string, string>
    {
        ["start_915cb944"] = "It's only when I hear the sounds of shuffling feet and supplies being put away that I realize that the lecture's over.",
        ["rightaway_cf214f74"] = "Hi there! How was class?",
        ["rightaway_8cb5deeb"] = "Sure, but what's a \"visual novel?\"",
        ["marry_2ad49007"] = "{b}Good Ending{/b}.",
    };

    /// <summary>The game's text font, as scripts name it (relative to <c>game/</c>).</summary>
    public const string TextFont = "gui/font/FixtureSans.ttf";

    /// <summary>Writes the game into <paramref name="dir"/> (created if missing; files of the same name are replaced).</summary>
    /// <param name="archived">Compiled scripts in <c>game/scripts.rpa</c> instead of loose <c>.rpyc</c> files.</param>
    /// <returns>Paths of the written files, relative to <paramref name="dir"/>.</returns>
    public static IReadOnlyList<string> Create(string dir, bool archived = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        var files = new Dictionary<string, byte[]>
        {
            ["Game.exe"] = Encoding.UTF8.GetBytes("Ren'Py launcher stub: not a real executable"),
            ["Game.sh"] = Encoding.UTF8.GetBytes("#!/bin/sh\necho 'Ren'Py launcher stub'\n"),
            ["Game.py"] = Encoding.UTF8.GetBytes("# Ren'Py launcher script (stub)\n"),
            ["renpy/__init__.py"] = Encoding.UTF8.GetBytes("# Ren'Py engine (stub)\n"),
            ["renpy/common/DejaVuSans.ttf"] = FontWriter.TrueType("DejaVu Sans", [.. FontWriter.Ascii, .. Enumerable.Range(0xA0, 0x60), .. Enumerable.Range(0x370, 0x90), .. Enumerable.Range(0x400, 0x100)]),
            ["game/script.rpy"] = Encoding.UTF8.GetBytes(ScriptSource),
            ["game/screens.rpy"] = Encoding.UTF8.GetBytes(ScreensSource),
        };
        var compiled = new Dictionary<string, byte[]>
        {
            ["script.rpyc"] = Compile(ScriptStatements()),
            ["screens.rpyc"] = Compile(ScreensStatements()),
            [TextFont] = FontWriter.TrueType("Fixture Sans", [.. FontWriter.Ascii, .. Enumerable.Range(0xA0, 0x60)]),
        };
        if (archived)
        {
            var archive = Path.Combine(dir, "game", "scripts.rpa");
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            RenPyArchive.Write(archive, compiled);
        }
        else
        {
            foreach (var (name, content) in compiled)
            {
                files[$"game/{name}"] = content;
            }
        }

        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        return [.. files.Keys.Concat(archived ? ["game/scripts.rpa"] : []).Order(StringComparer.Ordinal)];
    }

    private static byte[] Compile(List<object?> statements)
    {
        var data = new PyDict();
        data.Set("version", 5003000L);
        data.Set("key", "unlocked");
        return RenPyScriptFile.Write(new Dictionary<int, byte[]> { [1] = PickleWriter.Write(new PyTuple([data, statements])) });
    }

    private static List<object?> ScriptStatements()
    {
        var b = new Builder("game/script.rpy");
        return
        [
            b.Define("s", "Character(_(\"Sylvie\"), color=\"#c8ffc8\")"),
            b.Define("m", "Character(_(\"Me\"), color=\"#c8c8ff\")"),
            b.Label("start", false,
            [
                b.User("play music \"illurock.opus\""),
                b.Say(null, KnownIdentifiers["start_915cb944"]),
                b.Say(null, "The lecture hall empties.", with: "fade"),
                b.Menu(
                [
                    ("As soon as she catches my eye, I decide...", null),
                    ("To ask her right away.", [b.Jump("rightaway")]),
                    ("To ask her later.", [b.Say("m", "Maybe later..."), b.Jump("later")]),
                ]),
            ]),
            b.Label("rightaway", false,
            [
                b.Say("s", KnownIdentifiers["rightaway_cf214f74"]),
                b.Say("m", "Good..."),
                b.Say("m", "Good..."),
                b.User("voice \"sylvie_01.ogg\"", translatable: true),
                b.Say("s", "Welcome back, [player]! 100%% sure.", attributes: ["green", "smile"]),
                b.Say("s", KnownIdentifiers["rightaway_8cb5deeb"], interact: false),
                b.If(
                [
                    ("book", [b.Say("s", "So it's like a book?")]),
                    ("True", [b.Say("s", "Line one.\nLine two  with two spaces.")]),
                ]),
                b.Python("renpy.notify(_('Saved!'))"),
            ]),
            b.Label("_hidden_helper", true, [b.Say(null, "A line under a hidden label.")]),
            b.Label("marry", false, [b.Say(null, KnownIdentifiers["marry_2ad49007"])]),
            b.Label("later", false, [b.Say(null, "{b}Bad Ending{/b}.")]),
            b.Translate("russian", "later_f5219ac2", [b.Say(null, "{b}Плохая концовка{/b}")]),
        ];
    }

    private static List<object?> ScreensStatements()
    {
        var b = new Builder("game/screens.rpy");
        return
        [
            b.Screen("main_menu", "textbutton _(\"Start\") action Start()\ntextbutton __(\"Quit\") action Quit()"),
        ];
    }

    private const string ScriptSource = """
        define s = Character(_("Sylvie"), color="#c8ffc8")
        define m = Character(_("Me"), color="#c8c8ff")

        label start:
            play music "illurock.opus"
            "It's only when I hear the sounds of shuffling feet and supplies being put away that I realize that the lecture's over."
            "The lecture hall empties." with fade
            menu:
                "As soon as she catches my eye, I decide..."
                "To ask her right away.":
                    jump rightaway
                "To ask her later.":
                    m "Maybe later..."
                    jump later

        label rightaway:
            s "Hi there! How was class?"
            m "Good..."
            m "Good..."
            voice "sylvie_01.ogg"
            s green smile "Welcome back, [player]! 100%% sure."
            s "Sure, but what's a \"visual novel?\"" nointeract
            if book:
                s "So it's like a book?"
            else:
                s "Line one.\nLine two \ with two spaces."
            $ renpy.notify(_('Saved!'))

        label _hidden_helper hide:
            "A line under a hidden label."

        label marry:
            "{b}Good Ending{/b}."

        label later:
            "{b}Bad Ending{/b}."

        translate russian later_f5219ac2:
            "{b}Плохая концовка{/b}"

        """;

    private const string ScreensSource = """
        screen main_menu():
            textbutton _("Start") action Start()
            textbutton __("Quit") action Quit()

        """;

    /// <summary>Builds <c>renpy.ast</c> nodes as Ren'Py 7 pickles them (state dicts, <c>PyExpr</c> as a string subclass).</summary>
    private sealed class Builder(string file)
    {
        private int _line;

        public PyObject Say(string? who, string what, IReadOnlyList<string>? attributes = null, string? with = null, bool interact = true) =>
            Node("Say", ("who", who), ("what", what), ("attributes", attributes is null ? null : new PyTuple([.. attributes])),
                ("with_", with is null ? null : Expr(with)), ("interact", interact), ("who_fast", who is not null));

        public PyObject Label(string name, bool hide, List<object?> block) =>
            Node("Label", ("name", name), ("hide", hide), ("parameters", null), ("block", block));

        public PyObject Define(string name, string expression) =>
            Node("Define", ("varname", name), ("store", "store"), ("code", PyCode(expression, "eval")));

        public PyObject Python(string code) =>
            Node("Python", ("hide", false), ("store", "store"), ("code", PyCode(code, "exec")));

        public PyObject User(string line, bool translatable = false) =>
            Node("UserStatement", ("line", line), ("translatable", translatable), ("parsed", null));

        public PyObject Jump(string target) => Node("Jump", ("target", target), ("expression", false));

        public PyObject Menu(IReadOnlyList<(string Label, List<object?>? Block)> items) =>
            Node("Menu", ("items", items.Select(i => (object?)new PyTuple([i.Label, Expr("True"), i.Block])).ToList()), ("set", null), ("with_", null));

        public PyObject If(IReadOnlyList<(string Condition, List<object?> Block)> entries) =>
            Node("If", ("entries", entries.Select(e => (object?)new PyTuple([Expr(e.Condition), e.Block])).ToList()));

        public PyObject Translate(string language, string identifier, List<object?> block) =>
            Node("Translate", ("language", language), ("identifier", identifier), ("block", block));

        public PyObject Screen(string name, string source)
        {
            var screen = new PyObject(new PyGlobal("renpy.sl2.slast", "SLScreen"), new PyTuple([]))
            {
                State = Dict(("name", name), ("children", new List<object?>
                {
                    new PyObject(new PyGlobal("renpy.sl2.slast", "SLDisplayable"), new PyTuple([])) { State = Dict(("positional", new List<object?> { source })) },
                })),
            };
            return Node("Screen", ("screen", screen));
        }

        private PyObject PyCode(string source, string mode) =>
            new(new PyGlobal("renpy.ast", "PyCode"), new PyTuple([])) { State = new PyTuple([1L, Expr(source), new PyTuple([file, (long)_line, 0L]), mode]) };

        private PyObject Expr(string source) => new(new PyGlobal("renpy.ast", "PyExpr"), new PyTuple([source, file, (long)_line]));

        private PyObject Node(string type, params (string Name, object? Value)[] fields)
        {
            _line++;
            var state = Dict(fields);
            state.Set("filename", file);
            state.Set("linenumber", (long)_line);
            if (!state.Contains("name"))
            {
                // A label's node name is the label itself; other statements get (file, time, serial).
                state.Set("name", new PyTuple([file, 1700000000L, (long)_line]));
            }

            state.Set("next", null);
            return new PyObject(new PyGlobal("renpy.ast", type), new PyTuple([])) { State = state };
        }

        private static PyDict Dict(params (string Name, object? Value)[] fields)
        {
            var dict = new PyDict();
            foreach (var (name, value) in fields)
            {
                dict.Set(name, value);
            }

            return dict;
        }
    }
}
