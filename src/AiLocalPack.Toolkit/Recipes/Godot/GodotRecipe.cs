using System.Text;
using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Recipes.Godot;

/// <summary>
/// Recipe tool <c>godot</c> for Godot 3 and 4 games: everything lives in the game's main pack (<c>&lt;name&gt;.pck</c>,
/// or embedded in the executable, see <see cref="GodotPack"/>), and a rebuild writes that whole file again with the
/// changed resources (the install backs up the original). What is read:
/// <list type="bullet">
/// <item>A game with translation resources (<c>*.translation</c>, Godot's CSV import): the messages of its source
/// language (<c>en</c> / <c>en_*</c>; else its only language; else the one with the most messages), keyed
/// <c>tr:&lt;key&gt;</c> by the key the game passes to <c>tr()</c>. Compressed translations keep only hashes of their
/// keys (<see cref="GodotTranslation"/>): keys are found back among the strings of the game's own scenes, resources and
/// scripts; a message whose key no file names is keyed <c>tr#&lt;bucket&gt;:&lt;hash&gt;</c>. A rebuild writes the
/// translations into those resources, so the game shows them when set to its source language (English): a language
/// the game's menu lacks can't be added without changing its code.</item>
/// <item>A game without translations: the visible texts of its text scenes (<c>*.tscn</c>, <see cref="GodotTextScene"/>),
/// keyed <c>scene:&lt;scene path&gt;:&lt;node path&gt;:&lt;property&gt;</c>.</item>
/// </list>
/// Left out: texts set by scripts, binary scenes (<c>.scn</c>, Godot 4's export option), PO translations. An encrypted
/// pack, or an encrypted translation, is refused with the reason.
/// </summary>
public sealed partial class GodotRecipe : IRecipeTool
{
    public const string ToolId = "godot";

    private const string TranslationPrefix = "tr:";
    private const string HashedPrefix = "tr";
    private const string ScenePrefix = "scene:";

    /// <summary>Files read for the strings naming translation keys (scenes, resources, scripts, settings, data).</summary>
    private static readonly string[] KeySources = [".tscn", ".tres", ".gd", ".gdc", ".scn", ".res", ".cfg", ".json", ".csv", ".txt", ".binary", ".godot"];

    private const long MaxTextFile = 32L << 20;

    private readonly EngineDetector _engines = new();

    public string Id => ToolId;

    public Version Version { get; } = new(1, 0, 0);

    public GameEngine Engine => GameEngine.Godot;

    public RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default)
    {
        using var game = Load(gameDir, cancellationToken);
        var strings = game.Texts.Values
            .Select(t => new ExtractedString(t.Key, t.Original, t.Context, null, Placeholders(t.Original)))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();
        return new RecipeExtraction(Id, Version, strings, strings.Count == 0 ? [] : [game.PackPath]);
    }

    public RebuildResult Rebuild(
        string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(output);
        using var game = Load(gameDir, cancellationToken);
        var unknown = translations.Keys.Where(k => !game.Texts.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var chosen = game.Texts.Values
            .Where(t => translations.TryGetValue(t.Key, out var value) && value is { Length: > 0 } && value != t.Original)
            .ToList();

        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (file, translation) in game.Translations)
        {
            // A key names the message in every source translation; a hash only in the file it was read from.
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var byId = translation.Messages.ToDictionary(m => HashedPrefix + m.Id, StringComparer.Ordinal);
            foreach (var text in chosen.Where(t => t.Scene is null))
            {
                var message = text.Key.StartsWith(TranslationPrefix, StringComparison.Ordinal)
                    ? translation.Find(text.Key[TranslationPrefix.Length..])
                    : text.File == file ? byId.GetValueOrDefault(text.Key) : null;
                if (message is not null)
                {
                    texts.TryAdd(message.Id, translations[text.Key]);
                }
            }

            if (texts.Count > 0)
            {
                replacements[file] = Guard(file, () => translation.Write(texts));
            }
        }

        foreach (var group in chosen.Where(t => t.Scene is not null).GroupBy(t => t.File))
        {
            var scene = game.Scenes[group.Key];
            var replaced = GodotTextScene.Replace(scene, group.Select(t => (t.Scene!, translations[t.Key])));
            replacements[group.Key] = Encoding.UTF8.GetBytes(replaced);
        }

        if (replacements.Count == 0)
        {
            return new RebuildResult([], 0, unknown);
        }

        try
        {
            using var stream = output.Create(game.PackPath);
            game.Pack.Write(stream, replacements, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't write {game.PackPath}: {e.Message}", game.PackPath, e);
        }
        catch (GodotFormatException e)
        {
            throw new RecipeException($"Can't read {game.PackPath}: {e.Message}", game.PackPath, e);
        }

        return new RebuildResult([game.PackPath], chosen.Count, unknown);
    }

    public IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var game = Load(gameDir, CancellationToken.None);
        return keys.Any(game.Texts.ContainsKey) ? [game.PackPath] : [];
    }

    /// <summary>
    /// Markup in a Godot text that a translation keeps: BBCode tags (<c>[color=red]</c>, <c>[/b]</c>), <c>{name}</c>
    /// and <c>{0}</c> format fields, <c>%s</c> / <c>%d</c> / <c>%.1f</c>-style fields and <c>%%</c>.
    /// </summary>
    public static IReadOnlyList<string> Placeholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. Placeholder().Matches(text).Select(m => m.Value)];
    }

    [GeneratedRegex(@"\[/?[A-Za-z_][A-Za-z0-9_]*(?:[= ][^\[\]\n]*)?\]|\{[A-Za-z0-9_]*\}|%%|%[-+0#]*\d*(?:\.\d+)?[sdifcxXov]")]
    private static partial Regex Placeholder();

    private Game Load(string gameDir, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDir);
        var root = Path.GetFullPath(gameDir);
        var packPath = FindPack(root);
        GodotPack pack;
        try
        {
            pack = GodotPack.Open(Path.Combine(root, packPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GodotFormatException)
        {
            throw new RecipeException($"Can't read the game's pack {packPath}: {e.Message}", packPath, e);
        }

        try
        {
            return Read(pack, packPath, cancellationToken);
        }
        catch
        {
            pack.Dispose();
            throw;
        }
    }

    private static Game Read(GodotPack pack, string packPath, CancellationToken cancellationToken)
    {
        var entries = pack.Entries.Where(e => !e.Removal).GroupBy(e => e.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        byte[] Bytes(GodotPackEntry entry) => Guard(packPath, () => pack.Read(entry));

        var translations = new SortedDictionary<string, GodotTranslation>(StringComparer.Ordinal);
        var encrypted = new List<string>();
        foreach (var entry in entries.Values.Where(e => e.Path.EndsWith(".translation", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Encrypted)
            {
                encrypted.Add(entry.Path);
                continue;
            }

            try
            {
                translations[entry.Path] = GodotTranslation.Read(Bytes(entry));
            }
            catch (GodotFormatException e) when (e.Problem == GodotProblem.Unsupported)
            {
                // Another kind of translation (PO, plurals): its texts aren't read yet.
            }
            catch (GodotFormatException e)
            {
                throw new RecipeException($"Can't read the translation res://{entry.Path} in {packPath}: {e.Message}", packPath, e);
            }
        }

        if (translations.Count == 0 && encrypted.Count > 0)
        {
            throw new RecipeException($"The game's translations ({string.Join(", ", encrypted.Select(p => "res://" + p))}) are encrypted with the developer's key, so they can't be read.", packPath);
        }

        var source = SourceTranslations(translations);
        var texts = new Dictionary<string, GameText>(StringComparer.Ordinal);
        var scenes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.Count > 0)
        {
            if (source.Values.Any(t => t.IsHashed))
            {
                var candidates = Candidates(entries.Values, Bytes, cancellationToken);
                foreach (var translation in source.Values)
                {
                    translation.Recover(candidates);
                }
            }

            foreach (var (file, translation) in source)
            {
                foreach (var message in translation.Messages)
                {
                    var key = message.Key is { } name ? TranslationPrefix + name : HashedPrefix + message.Id;
                    var context = message.Key is { } known
                        ? $"Game text (translation key {known})"
                        : "Game text (its translation key isn't named in the game's files)";
                    texts.TryAdd(key, new GameText(key, message.Text, context, file, null));
                }
            }
        }
        else
        {
            foreach (var entry in entries.Values.Where(e => e.Path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) && !e.Encrypted && e.Size <= MaxTextFile).OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scene = Encoding.UTF8.GetString(Bytes(entry));
                var found = GodotTextScene.Read(scene);
                if (found.Count == 0)
                {
                    continue;
                }

                scenes[entry.Path] = scene;
                foreach (var text in found)
                {
                    var key = $"{ScenePrefix}{entry.Path}:{text.Node}:{text.Property}";
                    var context = $"{GodotTextScene.Properties[text.Property]} of the {text.Type ?? "instanced scene"} node {text.Node} in {entry.Path}";
                    texts.TryAdd(key, new GameText(key, text.Value, context, entry.Path, text));
                }
            }
        }

        return new Game(pack, packPath, texts, source, scenes);
    }

    /// <summary>The translations of the game's source language: <c>en</c> / <c>en_*</c>, else the only language, else the one with the most messages.</summary>
    private static SortedDictionary<string, GodotTranslation> SourceTranslations(SortedDictionary<string, GodotTranslation> all)
    {
        static bool English(string locale) => locale == "en" || locale.StartsWith("en_", StringComparison.Ordinal) || locale.StartsWith("en-", StringComparison.Ordinal);
        var locales = all.Values.GroupBy(t => t.Locale, StringComparer.Ordinal).ToList();
        var chosen = locales.FirstOrDefault(g => g.Key == "en")?.Key
            ?? locales.Where(g => English(g.Key)).Select(g => g.Key).Order(StringComparer.Ordinal).FirstOrDefault()
            ?? locales.OrderByDescending(g => g.Sum(t => t.Messages.Count)).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        return new SortedDictionary<string, GodotTranslation>(
            all.Where(p => p.Value.Locale == chosen).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Strings that may be translation keys: from each scene, resource, script and data file of the pack, every run of
    /// printable text, its parts between quotes and its identifier-like tokens (UTF-8 bytes).
    /// </summary>
    private static HashSet<byte[]> Candidates(IEnumerable<GodotPackEntry> entries, Func<GodotPackEntry, byte[]> read, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<byte[]>(ByteArrayComparer.Instance);
        foreach (var entry in entries)
        {
            if (entry.Encrypted || entry.Size > MaxTextFile || !KeySources.Any(s => entry.Path.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = read(entry);
            var start = -1;
            for (var i = 0; i <= bytes.Length; i++)
            {
                var printable = i < bytes.Length && bytes[i] >= 0x20 && bytes[i] != 0x7F;
                if (printable && start < 0)
                {
                    start = i;
                }
                else if (!printable && start >= 0)
                {
                    AddRun(candidates, bytes.AsSpan(start, i - start));
                    start = -1;
                }
            }
        }

        return candidates;
    }

    private static void AddRun(HashSet<byte[]> candidates, ReadOnlySpan<byte> run)
    {
        if (run.Length < 2 || run.Length > 512)
        {
            return;
        }

        candidates.Add(run.ToArray());
        foreach (var range in run.Split((byte)'"'))
        {
            if (range.End.Value - range.Start.Value is >= 2 and var length && length != run.Length)
            {
                candidates.Add(run[range].ToArray());
            }
        }

        var tokenStart = -1;
        for (var i = 0; i <= run.Length; i++)
        {
            var word = i < run.Length && (char.IsAsciiLetterOrDigit((char)run[i]) || run[i] is (byte)'_' or (byte)'.' or (byte)'-');
            if (word && tokenStart < 0)
            {
                tokenStart = i;
            }
            else if (!word && tokenStart >= 0)
            {
                if (i - tokenStart >= 2 && i - tokenStart != run.Length)
                {
                    candidates.Add(run[tokenStart..i].ToArray());
                }

                tokenStart = -1;
            }
        }
    }

    /// <summary>The main pack, relative to the game folder: the engine marker, or the pack named like the main executable next to it.</summary>
    private string FindPack(string root)
    {
        var detection = _engines.Detect(root);
        if (detection.Match is not { Kind.Engine: GameEngine.Godot } match)
        {
            throw new RecipeException(Directory.Exists(root)
                ? $"{root} is not a Godot game (found: {detection.Kind.DisplayName})."
                : $"Folder not found: {root}");
        }

        var marker = match.Marker;
        if (!marker.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) || match.MainExecutable is not { } executable)
        {
            return marker;
        }

        var name = Path.GetFileName(executable.TrimEnd('/'));
        var stem = name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? name[..^4] : Path.GetFileNameWithoutExtension(name);
        var folder = marker.Contains('/', StringComparison.Ordinal) ? marker[..marker.LastIndexOf('/')] : string.Empty;
        var bundled = name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? $"{executable.TrimEnd('/')}/Contents/Resources/{stem}.pck" : null;
        var sibling = (folder.Length == 0 ? string.Empty : folder + "/") + stem + ".pck";
        return new[] { bundled, sibling }.FirstOrDefault(p => p is not null && GodotPack.IsPack(Path.Combine(root, p))) ?? marker;
    }

    private static T Guard<T>(string file, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GodotFormatException)
        {
            throw new RecipeException($"Can't read {file}: {e.Message}", file, e);
        }
    }

    /// <param name="File">The resource holding it (path in the pack).</param>
    /// <param name="Scene">For a scene text, where it is in the scene.</param>
    private sealed record GameText(string Key, string Original, string Context, string File, SceneText? Scene);

    private sealed record Game(
        GodotPack Pack,
        string PackPath,
        IReadOnlyDictionary<string, GameText> Texts,
        IReadOnlyDictionary<string, GodotTranslation> Translations,
        IReadOnlyDictionary<string, string> Scenes) : IDisposable
    {
        public void Dispose() => Pack.Dispose();
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = default(HashCode);
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
