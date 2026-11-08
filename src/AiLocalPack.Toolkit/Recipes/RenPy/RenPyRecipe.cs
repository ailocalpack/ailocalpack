using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>
/// Recipe tool <c>renpy</c>: packs install through Ren'Py's own translation system, so no game file changes, files are
/// only added. Strings come from the compiled scripts (<c>.rpyc</c>, loose under <c>game/</c> or inside the
/// <c>game/*.rpa</c> archives), read with a pickle reader that never runs code (<see cref="PickleReader"/>); shipped
/// games always have them, <c>.rpy</c> sources are optional. Keys: a dialogue line = <c>say:&lt;Ren'Py identifier&gt;</c>
/// (<c>say:start_915cb944</c>, the identifier Ren'Py's own <c>translate</c> output uses); a menu choice or
/// <c>_()</c> text = <c>string:&lt;first 16 hex of its SHA-256&gt;</c> (Ren'Py translates those by text). Rebuild writes
/// <c>game/tl/ailocalpack_&lt;tag&gt;/&lt;script&gt;.rpy</c> (dialogue blocks per script), <c>…/strings.rpy</c> and
/// <c>game/tl/None/ailocalpack_language.rpy</c>, which starts the game in that language (<c>config.language</c>); Ren'Py
/// compiles each into a <c>.rpyc</c> next to it on start (<see cref="RebuildResult.GeneratedFiles"/>).
/// Fonts (<see cref="IGameFonts"/>): every font file of the game (loose under <c>game/</c>, in its archives, and
/// Ren'Py's own <c>renpy/common</c> ones such as <c>DejaVuSans.ttf</c>), named as scripts name them. A swap puts the
/// replacement under <c>game/tl/&lt;language&gt;/ailocalpack_fonts/</c> and writes
/// <c>game/tl/&lt;language&gt;/ailocalpack_fonts.rpy</c>, a <c>translate &lt;language&gt; python</c> block that maps each
/// swapped font (all four bold / italic combinations) to it in <c>config.font_replacement_map</c>: only in that
/// language, and for every style and screen whatever variable named the font.
/// </summary>
public sealed class RenPyRecipe : IRecipeTool, IGameFonts
{
    public const string ToolId = "renpy";

    /// <summary>The file that switches the game to the pack's language, relative to the game folder's <c>game/</c>.</summary>
    public const string LanguageFile = "tl/None/ailocalpack_language.rpy";

    private const string SayPrefix = "say:";
    private const string StringPrefix = "string:";
    private const string StringsFile = "strings.rpy";

    /// <summary>Folder (under the language's <c>tl/</c> folder) of swapped-in fonts, and the name of the script mapping them.</summary>
    private const string FontsName = "ailocalpack_fonts";

    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    /// <summary>Folders of <c>game/</c> whose scripts aren't the game's text (translations, Ren'Py's cache, saves).</summary>
    private static readonly string[] SkippedFolders = ["tl", "cache", "saves"];

    private readonly EngineDetector _engines = new();

    public string Id => ToolId;

    public Version Version { get; } = new(1, 0, 0);

    public GameEngine Engine => GameEngine.RenPy;

    public TextMarkup Markup => RenPyTranslation.Markup;

    public RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default)
    {
        var game = Load(gameDir, cancellationToken);
        var strings = new List<ExtractedString>();
        foreach (var dialogue in game.Dialogues.Values)
        {
            var say = dialogue.Say;
            var speaker = string.IsNullOrEmpty(say.Who) ? "Narration" : $"Line said by {say.Who}";
            var where = dialogue.Label is null ? string.Empty : $" (label {dialogue.Label})";
            strings.Add(new ExtractedString(SayPrefix + dialogue.Identifier, say.What, speaker + where, null, RenPyTranslation.Placeholders(say.What)));
        }

        foreach (var (key, text) in game.Strings)
        {
            var note = text.MenuChoice ? "Menu choice" : "Interface text";
            strings.Add(new ExtractedString(key, text.Text, note, null, RenPyTranslation.Placeholders(text.Text)));
        }

        return new RecipeExtraction(
            Id,
            Version,
            [.. strings.OrderBy(s => s.Key, StringComparer.Ordinal)],
            [.. game.Sources.Order(StringComparer.Ordinal)]);
    }

    public RebuildResult Rebuild(
        string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(output);
        var game = Load(gameDir, cancellationToken);
        var name = RenPyTranslation.LanguageName(language);
        var blocks = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
        var strings = new StringBuilder();
        var applied = 0;
        var unknown = translations.Keys.Where(k => !game.Dialogues.ContainsKey(k) && !game.Strings.ContainsKey(k)).ToList();

        // In the game's order (load order, then statement order), so the files read like the scripts.
        foreach (var (key, dialogue) in game.Dialogues)
        {
            if (translations.TryGetValue(key, out var translation) && translation is { Length: > 0 } && translation != dialogue.Say.What)
            {
                if (!blocks.TryGetValue(dialogue.Script, out var file))
                {
                    blocks[dialogue.Script] = file = new StringBuilder();
                }

                WriteBlock(file, name, dialogue, translation);
                applied++;
            }
        }

        foreach (var (key, text) in game.Strings)
        {
            if (translations.TryGetValue(key, out var translation) && translation is { Length: > 0 } && translation != text.Text)
            {
                strings.Append("    old ").Append(RenPyTranslation.EncodeSayString(text.Text)).Append('\n')
                    .Append("    new ").Append(RenPyTranslation.EncodeSayString(translation)).Append("\n\n");
                applied++;
            }
        }

        var files = new List<string>();
        void Write(string path, string content)
        {
            try
            {
                output.Write($"{game.GamePath}/{path}", Encoding.UTF8.GetBytes(content));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new RecipeException($"Can't write the translation file {path}: {e.Message}", $"{game.GamePath}/{path}", e);
            }

            files.Add($"{game.GamePath}/{path}");
        }

        foreach (var (script, content) in blocks)
        {
            Write($"tl/{name}/{script}", Header + content);
        }

        if (strings.Length > 0)
        {
            Write($"tl/{name}/{StringsFile}", $"{Header}translate {name} strings:\n\n{strings}");
        }

        if (files.Count > 0)
        {
            Write(LanguageFile, $"""
                # Written by ailocalpack: starts the game in the installed pack's language. Uninstalling the pack removes this file.
                init 999 python:
                    config.language = "{name}"

                """);
        }

        return new RebuildResult(
            [.. files.Order(StringComparer.Ordinal)],
            applied,
            [.. unknown.Order(StringComparer.Ordinal)])
        {
            GeneratedFiles = [.. files.Select(f => f + "c").Order(StringComparer.Ordinal)],
        };
    }

    public IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var game = Load(gameDir, CancellationToken.None);
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (game.Dialogues.TryGetValue(key, out var dialogue))
            {
                sources.Add(dialogue.Source);
            }
            else if (game.Strings.TryGetValue(key, out var text))
            {
                sources.Add(text.Source);
            }
        }

        return [.. sources];
    }

    /// <summary>Ren'Py picks bold faces by file (<c>config.font_replacement_map</c>), so swaps bring the bold face too.</summary>
    public bool UsesBold => true;

    public IReadOnlyList<GameFont> Fonts(string gameDir)
    {
        var (root, gamePath, projectPath) = FindGameFolder(gameDir);
        var gameFolder = Path.Combine(root, gamePath);
        var fonts = new SortedDictionary<string, GameFont>(StringComparer.Ordinal);

        // Ren'Py's search order: loose files of game/, its archives, then renpy/common.
        foreach (var file in List(() => Directory.EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories), gamePath))
        {
            var entry = Path.GetRelativePath(gameFolder, file).Replace('\\', '/');
            if (IsFont(entry) && !IsSkipped(entry))
            {
                fonts.TryAdd(entry, new GameFont(entry, $"{gamePath}/{entry}"));
            }
        }

        foreach (var archivePath in List(() => Directory.EnumerateFiles(gameFolder, "*.rpa"), gamePath))
        {
            var relative = $"{gamePath}/{Path.GetFileName(archivePath)}";
            RenPyArchive archive;
            try
            {
                archive = RenPyArchive.Open(archivePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RenPyFormatException)
            {
                throw new RecipeException($"Can't read the archive {relative}: {e.Message}", relative, e);
            }

            foreach (var entry in archive.Names.Where(n => IsFont(n) && !IsSkipped(n)))
            {
                fonts.TryAdd(entry, new GameFont(entry, relative, entry));
            }
        }

        var common = Path.Combine(root, projectPath, "renpy", "common");
        if (Directory.Exists(common))
        {
            var commonPath = (projectPath.Length == 0 ? string.Empty : projectPath + "/") + "renpy/common";
            foreach (var file in List(() => Directory.EnumerateFiles(common), commonPath))
            {
                var name = Path.GetFileName(file);
                if (IsFont(name))
                {
                    fonts.TryAdd(name, new GameFont(name, $"{commonPath}/{name}"));
                }
            }
        }

        return [.. fonts.Values];
    }

    public byte[] ReadFont(string gameDir, GameFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var path = Path.Combine(Path.GetFullPath(gameDir), font.File);
        return font.Entry is { } entry
            ? Guard(font.File, () => RenPyArchive.Open(path).Read(entry))
            : Guard(font.File, () => File.ReadAllBytes(path));
    }

    public IReadOnlyList<string> Placeholders(string text) => RenPyTranslation.Placeholders(text);

    public FontSwapFiles Swap(string gameDir, string language, IReadOnlyList<FontSwap> swaps, IRebuildOutput output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(swaps);
        ArgumentNullException.ThrowIfNull(output);
        if (swaps.Count == 0)
        {
            return new FontSwapFiles([], []);
        }

        var (_, gamePath, _) = FindGameFolder(gameDir);
        var name = RenPyTranslation.LanguageName(language);
        var folder = $"tl/{name}/{FontsName}";
        var files = new List<string>();
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        string Put(WhitelistFont font, FontFace face, byte[] content)
        {
            var path = $"{folder}/{font.File(face)!.FileName}";
            if (written.TryAdd(path, path))
            {
                Write(output, $"{gamePath}/{path}", content);
                files.Add($"{gamePath}/{path}");
            }

            return path;
        }

        var script = new StringBuilder()
            .Append("# Written by ailocalpack: the game's fonts lack letters of this translation, so its text is drawn with\n")
            .Append("# open fonts (SIL Open Font License) in this language only. Uninstalling the pack removes this file.\n")
            .Append("translate ").Append(name).Append(" python:\n");
        foreach (var swap in swaps)
        {
            var regular = Put(swap.Replacement, FontFace.Regular, swap.Regular);
            var bold = swap.Bold is { } boldBytes ? Put(swap.Replacement, FontFace.Bold, boldBytes) : null;
            foreach (var (isBold, isItalic) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                // With a bold file the face is real; without one Ren'Py makes it bold, as it does italics.
                var (target, makeBold) = isBold && bold is not null ? (bold, false) : (regular, isBold);
                script.Append("    config.font_replacement_map[").Append(PythonString(swap.Font.Name)).Append(", ").Append(Bool(isBold)).Append(", ").Append(Bool(isItalic))
                    .Append("] = (").Append(PythonString(target)).Append(", ").Append(Bool(makeBold)).Append(", ").Append(Bool(isItalic)).Append(")\n");
            }
        }

        var scriptPath = $"{gamePath}/tl/{name}/{FontsName}.rpy";
        Write(output, scriptPath, Encoding.UTF8.GetBytes(script.ToString()));
        files.Add(scriptPath);
        return new FontSwapFiles([.. files.Order(StringComparer.Ordinal)], [scriptPath + "c"]);
    }

    private static bool IsFont(string entry) => FontExtensions.Any(e => entry.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsSkipped(string entry) => SkippedFolders.Any(f => entry.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));

    private static string Bool(bool value) => value ? "True" : "False";

    /// <summary>A Python string literal (double quotes; backslashes, quotes and non-printable characters escaped).</summary>
    private static string PythonString(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            _ = c switch
            {
                '\\' => builder.Append("\\\\"),
                '"' => builder.Append("\\\""),
                < ' ' => builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}"),
                _ => builder.Append(c),
            };
        }

        return builder.Append('"').ToString();
    }

    private static void Write(IRebuildOutput output, string path, byte[] content)
    {
        try
        {
            output.Write(path, content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't write {path}: {e.Message}", path, e);
        }
    }

    private const string Header = "# Translation installed by ailocalpack, rebuilt from the game's own scripts. Uninstalling the pack removes this file.\n\n";

    private static void WriteBlock(StringBuilder file, string language, RenPyDialogue dialogue, string translation)
    {
        if (dialogue.Location is { } location)
        {
            file.Append("# ").Append(location).Append('\n');
        }

        file.Append("translate ").Append(language).Append(' ').Append(dialogue.Identifier).Append(":\n\n");
        foreach (var statement in dialogue.Block)
        {
            if (statement.Say is { } say)
            {
                file.Append("    # ").Append(RenPyTranslation.SayCode(say, withIdentifier: false)).Append('\n');
                file.Append("    ").Append(RenPyTranslation.SayCode(say, translation, withIdentifier: false)).Append('\n');
            }
            else
            {
                file.Append("    ").Append(statement.Code).Append('\n');
            }
        }

        file.Append('\n');
    }

    /// <summary>Every script of the game in Ren'Py's load order, read into dialogue blocks and strings by key.</summary>
    private Game Load(string gameDir, CancellationToken cancellationToken)
    {
        var (root, gamePath, _) = FindGameFolder(gameDir);
        var gameFolder = Path.Combine(root, gamePath);
        var scripts = new SortedDictionary<string, Func<byte[]>>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        // Archives first: a loose file of the same name wins, as in Ren'Py.
        foreach (var archivePath in List(() => Directory.EnumerateFiles(gameFolder, "*.rpa"), gamePath))
        {
            var relative = $"{gamePath}/{Path.GetFileName(archivePath)}";
            RenPyArchive archive;
            try
            {
                archive = RenPyArchive.Open(archivePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RenPyFormatException)
            {
                throw new RecipeException($"Can't read the archive {relative}: {e.Message}", relative, e);
            }

            foreach (var entry in archive.Names.Where(IsScript))
            {
                var name = entry;
                scripts[Script(name)] = () => Guard(relative, () => archive.Read(name));
                sources[Script(name)] = relative;
            }
        }

        foreach (var file in List(() => Directory.EnumerateFiles(gameFolder, "*.rpyc", SearchOption.AllDirectories), gamePath))
        {
            var entry = Path.GetRelativePath(gameFolder, file).Replace('\\', '/');
            if (!IsScript(entry))
            {
                continue;
            }

            var relative = $"{gamePath}/{entry}";
            scripts[Script(entry)] = () => Guard(relative, () => File.ReadAllBytes(file));
            sources[Script(entry)] = relative;
        }

        if (scripts.Count == 0)
        {
            throw new RecipeException($"{gameFolder} has no compiled scripts (.rpyc). Launch the game once so Ren'Py compiles them, then try again.");
        }

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var dialogues = new OrderedDictionary<string, RenPyDialogue>(StringComparer.Ordinal);
        var strings = new OrderedDictionary<string, RenPyString>(StringComparer.Ordinal);
        var used = new SortedSet<string>(StringComparer.Ordinal);

        // Ren'Py's load order: names from "A" on after the others, then by name.
        foreach (var (script, read) in scripts.OrderBy(s => string.CompareOrdinal(Stem(s.Key), "A") >= 0 ? 1 : 0).ThenBy(s => Stem(s.Key), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[script];
            IReadOnlyList<object?> statements;
            try
            {
                statements = RenPyScript.Statements(read());
            }
            catch (RenPyFormatException e)
            {
                throw new RecipeException($"Can't read the compiled script {script}c in {source}: {e.Message}", source, e);
            }

            var (found, texts) = RenPyScript.Read(statements, script, source, taken);
            foreach (var dialogue in found)
            {
                dialogues.TryAdd(SayPrefix + dialogue.Identifier, dialogue);
                used.Add(source);
            }

            foreach (var text in texts)
            {
                var key = StringPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Text)))[..16];
                if (!strings.TryGetValue(key, out var known) || (text.MenuChoice && !known.MenuChoice))
                {
                    strings[key] = known is null ? text : text with { Source = known.Source };
                }

                used.Add(strings[key].Source);
            }
        }

        return new Game(gamePath, dialogues, strings, used);
    }

    private static T Guard<T>(string file, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't read {file}: {e.Message}", file, e);
        }
        catch (RenPyFormatException e)
        {
            throw new RecipeException($"Can't read {file}: {e.Message}", file, e);
        }
    }

    private static List<string> List(Func<IEnumerable<string>> list, string gamePath)
    {
        try
        {
            return [.. list().Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't list {gamePath}: {e.Message}", null, e);
        }
    }

    /// <summary>A game script (<c>.rpyc</c>) outside translations, the cache and saves.</summary>
    private static bool IsScript(string entry) => entry.EndsWith(".rpyc", StringComparison.OrdinalIgnoreCase) && !IsSkipped(entry);

    /// <summary><c>dir/name.rpyc</c> → <c>dir/name.rpy</c>.</summary>
    private static string Script(string entry) => entry[..^1];

    private static string Stem(string script) => script[..^".rpy".Length];

    /// <summary>The folder holding <c>renpy/</c> and <c>game/</c> (from the engine marker) and <c>game/</c> relative to the game folder.</summary>
    private (string Root, string GamePath, string ProjectPath) FindGameFolder(string gameDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDir);
        var root = Path.GetFullPath(gameDir);
        var detection = _engines.Detect(root);
        if (detection.Match is not { Kind.Engine: GameEngine.RenPy } match)
        {
            throw new RecipeException(Directory.Exists(root)
                ? $"{root} is not a Ren'Py game (found: {detection.Kind.DisplayName})."
                : $"Folder not found: {root}");
        }

        // The marker is <project>/renpy[/<engine file>].
        var parts = match.Marker.Split('/');
        var renpy = Array.FindLastIndex(parts, p => p.Equals("renpy", StringComparison.OrdinalIgnoreCase));
        var project = string.Join('/', parts[..Math.Max(renpy, 0)]);
        var projectDir = project.Length == 0 ? root : Path.Combine(root, project);
        var game = FindChild(projectDir, "game") ?? throw new RecipeException($"{root} has no game folder.");
        var gamePath = (project.Length == 0 ? string.Empty : project + "/") + Path.GetFileName(game);
        return (root, gamePath, project);
    }

    private static string? FindChild(string dir, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <param name="GamePath">The <c>game</c> folder relative to the game folder (<c>game</c>, or inside a macOS bundle).</param>
    /// <param name="Sources">Game files holding text (relative to the game folder).</param>
    private sealed record Game(
        string GamePath,
        IReadOnlyDictionary<string, RenPyDialogue> Dialogues,
        IReadOnlyDictionary<string, RenPyString> Strings,
        IReadOnlyCollection<string> Sources);
}
