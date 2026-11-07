using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Fonts;

namespace AiLocalPack.Toolkit.Recipes.RpgMaker;

/// <summary>
/// Recipe tool <c>rpgmaker</c> for RPG Maker MV (<c>www/data</c>) and MZ (<c>data</c>), macOS bundles included
/// (<c>Contents/Resources/app.nw</c>). Text lives in the JSON files of the <c>data</c> folder (see
/// <see cref="RpgMakerStrings"/>). Key = <c>&lt;path from the project folder&gt;:&lt;JSON Pointer&gt;</c>, e.g.
/// <c>data/Map001.json:/events/1/pages/0/list/1/parameters/0</c>: the project folder is the one holding <c>js/</c> and
/// <c>data/</c>, so the same game has the same keys in its Windows, Linux and macOS builds. Rebuilding replaces only
/// the translated string values, keeping the rest of each file byte for byte.
/// Fonts (<see cref="IGameFonts"/>): the text font only, MZ's <c>advanced.mainFontFilename</c> of <c>System.json</c>
/// (the number font draws digits) and MV's <c>GameFont</c> face of <c>fonts/gamefont.css</c>; a swap adds the
/// replacement as <c>fonts/ailocalpack-&lt;file&gt;</c> and points that setting at it.
/// </summary>
public sealed partial class RpgMakerRecipe : IRecipeTool, IGameFonts
{
    public const string ToolId = "rpgmaker";

    private const string DataFolder = "data";
    private const string FontsFolder = "fonts";
    private const string MvFontCss = "gamefont.css";
    private const string MzFontPointer = "/advanced/mainFontFilename";
    private const string SwapPrefix = "ailocalpack-";

    private readonly EngineDetector _engines = new();

    public string Id => ToolId;

    public Version Version { get; } = new(1, 0, 0);

    public GameEngine Engine => GameEngine.RpgMaker;

    public RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default)
    {
        var project = FindProject(gameDir);
        var strings = new List<ExtractedString>();
        var files = new List<string>();
        foreach (var file in project.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = Read(project, file).Strings;
            if (found.Count == 0)
            {
                continue;
            }

            files.Add(file.GamePath);
            strings.AddRange(found.Select(s => new ExtractedString(
                Key(file, s.Pointer), s.Text, s.Context, null, RpgMakerStrings.Placeholders(s.Text))));
        }

        return new RecipeExtraction(
            Id,
            Version,
            [.. strings.OrderBy(s => s.Key, StringComparer.Ordinal)],
            [.. files.Order(StringComparer.Ordinal)]);
    }

    public RebuildResult Rebuild(string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(output);
        var project = FindProject(gameDir);
        var byFile = Group(translations.Keys);
        var unknown = new List<string>();
        var changed = new List<string>();
        var applied = 0;
        foreach (var (projectPath, keys) in byFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Find(projectPath) is not { } file)
            {
                unknown.AddRange(keys);
                continue;
            }

            var (bytes, found) = Read(project, file);
            var originals = found.ToDictionary(s => s.Pointer, s => s.Text, StringComparer.Ordinal);
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var pointer = key[(projectPath.Length + 1)..];
                if (!originals.TryGetValue(pointer, out var original))
                {
                    unknown.Add(key);
                }
                else if (translations[key] is { Length: > 0 } text && text != original)
                {
                    replacements[pointer] = text;
                }
            }

            if (replacements.Count == 0)
            {
                continue;
            }

            byte[] rebuilt;
            try
            {
                rebuilt = JsonStrings.Replace(bytes, replacements);
            }
            catch (JsonException e)
            {
                throw new RecipeException($"{file.GamePath} is not valid JSON: {e.Message}", file.GamePath, e);
            }

            try
            {
                output.Write(file.GamePath, rebuilt);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new RecipeException($"Can't write the rebuilt {file.GamePath}: {e.Message}", file.GamePath, e);
            }

            applied += replacements.Count;
            changed.Add(file.GamePath);
        }

        return new RebuildResult(
            [.. changed.Order(StringComparer.Ordinal)],
            applied,
            [.. unknown.Order(StringComparer.Ordinal)]);
    }

    public IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var project = FindProject(gameDir);
        return [.. Group(keys).Keys
            .Select(project.Find)
            .OfType<DataFile>()
            .Select(f => f.GamePath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The canvas draws bold by itself: one face is enough.</summary>
    public bool UsesBold => false;

    public IReadOnlyList<GameFont> Fonts(string gameDir)
    {
        var project = FindProject(gameDir);
        var name = project.IsMz ? MzFontName(project, ReadText(project, project.Path(DataFolder, "System.json"))) : MvFontName(project);
        if (name is null)
        {
            return [];
        }

        var file = project.Path(FontsFolder, name);
        return File.Exists(Path.Combine(project.GameDir, file)) ? [new GameFont(name, file)] : [];
    }

    public byte[] ReadFont(string gameDir, GameFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var path = Path.Combine(Path.GetFullPath(gameDir), font.File);
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't read the font {font.File}: {e.Message}", font.File, e);
        }
    }

    public IReadOnlyList<string> Placeholders(string text) => RpgMakerStrings.Placeholders(text);

    public FontSwapFiles Swap(string gameDir, string language, IReadOnlyList<FontSwap> swaps, IRebuildOutput output)
    {
        ArgumentNullException.ThrowIfNull(swaps);
        ArgumentNullException.ThrowIfNull(output);
        if (swaps.Count == 0)
        {
            return new FontSwapFiles([], []);
        }

        // One text font per game.
        var swap = swaps[0];
        var project = FindProject(gameDir);
        var fileName = SwapPrefix + swap.Replacement.File(Core.Fonts.FontFace.Regular)!.FileName;
        var fontPath = project.Path(FontsFolder, fileName);
        var files = new List<string> { fontPath };
        Write(output, fontPath, swap.Regular);
        if (project.IsMz)
        {
            var system = project.Path(DataFolder, "System.json");
            var bytes = Current(project, output, system);
            byte[] edited;
            try
            {
                edited = JsonStrings.Replace(bytes, new Dictionary<string, string> { [MzFontPointer] = fileName });
            }
            catch (JsonException e)
            {
                throw new RecipeException($"{system} is not valid JSON: {e.Message}", system, e);
            }

            Write(output, system, edited);
            files.Add(system);
        }
        else
        {
            var css = project.Path(FontsFolder, MvFontCss);
            var text = Encoding.UTF8.GetString(Current(project, output, css));
            var face = GameFontFace().Match(text);
            if (!face.Success)
            {
                throw new RecipeException($"{css} has no GameFont face to point at the new font.", css);
            }

            var url = FontUrl().Match(face.Value);
            var edited = text[..(face.Index + url.Index)] + $"url(\"{fileName}\")" + text[(face.Index + url.Index + url.Length)..];
            Write(output, css, Encoding.UTF8.GetBytes(edited));
            files.Add(css);
        }

        return new FontSwapFiles([.. files.Order(StringComparer.Ordinal)], []);
    }

    /// <summary>MZ: the main font file named in <c>System.json</c>, or <c>null</c>.</summary>
    private static string? MzFontName(Project project, byte[] system)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonStrings.WithoutBom(system), JsonStrings.DocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("advanced", out var advanced)
                && advanced.ValueKind == JsonValueKind.Object
                && advanced.TryGetProperty("mainFontFilename", out var name)
                && name.ValueKind == JsonValueKind.String
                && SafeName(name.GetString()) is { } file
                ? file
                : null;
        }
        catch (JsonException e)
        {
            var path = project.Path(DataFolder, "System.json");
            throw new RecipeException($"{path} is not valid JSON: {e.Message}", path, e);
        }
    }

    /// <summary>MV: the file of the <c>GameFont</c> face in <c>fonts/gamefont.css</c>, or <c>null</c>.</summary>
    private static string? MvFontName(Project project)
    {
        var css = project.Path(FontsFolder, MvFontCss);
        if (!File.Exists(Path.Combine(project.GameDir, css)))
        {
            return null;
        }

        var face = GameFontFace().Match(Encoding.UTF8.GetString(ReadText(project, css)));
        return face.Success && FontUrl().Match(face.Value) is { Success: true } url ? SafeName(Uri.UnescapeDataString(url.Groups["url"].Value)) : null;
    }

    /// <summary>A file name in <c>fonts/</c> (no folders, no query), else <c>null</c>.</summary>
    private static string? SafeName(string? name) =>
        name is { Length: > 0 } && name.IndexOfAny(['/', '\\', '?', '#', ':']) < 0 && name is not ("." or "..") ? name : null;

    private static byte[] ReadText(Project project, string path)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(project.GameDir, path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't read {path}: {e.Message}", path, e);
        }
    }

    /// <summary>The file as the rebuild wrote it, else the game's copy.</summary>
    private static byte[] Current(Project project, IRebuildOutput output, string path) => output.Read(path) ?? ReadText(project, path);

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

    /// <summary>An <c>@font-face</c> block whose family is <c>GameFont</c>.</summary>
    [GeneratedRegex(@"@font-face\s*\{[^}]*font-family\s*:\s*[""']?GameFont[""']?\s*;[^}]*\}", RegexOptions.IgnoreCase)]
    private static partial Regex GameFontFace();

    [GeneratedRegex(@"url\(\s*[""']?(?<url>[^""')]+?)[""']?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex FontUrl();

    private static string Key(DataFile file, string pointer) => $"{file.ProjectPath}:{pointer}";

    /// <summary>Keys by their file part (before the first <c>:</c>); keys without a pointer go under an empty file.</summary>
    private static SortedDictionary<string, List<string>> Group(IEnumerable<string> keys)
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var colon = key.IndexOf(":/", StringComparison.Ordinal);
            var file = colon < 0 ? string.Empty : key[..colon];
            if (!result.TryGetValue(file, out var list))
            {
                result[file] = list = [];
            }

            list.Add(key);
        }

        return result;
    }

    private static (byte[] Bytes, List<RpgMakerString> Strings) Read(Project project, DataFile file)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(project.GameDir, file.GamePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't read {file.GamePath}: {e.Message}", file.GamePath, e);
        }

        try
        {
            using var document = JsonDocument.Parse(JsonStrings.WithoutBom(bytes), JsonStrings.DocumentOptions);
            return (bytes, RpgMakerStrings.Extract(file.Name, document.RootElement));
        }
        catch (JsonException e)
        {
            throw new RecipeException($"{file.GamePath} is not valid JSON: {e.Message}", file.GamePath, e);
        }
    }

    /// <summary>The project folder (holding <c>js/</c> and <c>data/</c>) and its text files.</summary>
    private Project FindProject(string gameDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDir);
        var root = Path.GetFullPath(gameDir);
        var detection = _engines.Detect(root);
        if (detection.Match is not { Kind.Engine: GameEngine.RpgMaker } match)
        {
            throw new RecipeException(Directory.Exists(root)
                ? $"{root} is not an RPG Maker MV / MZ game (found: {detection.Kind.DisplayName})."
                : $"Folder not found: {root}");
        }

        // The marker is <project>/js/<core>.js.
        var parts = match.Marker.Split('/');
        var project = string.Join('/', parts[..^2]);
        var projectDir = project.Length == 0 ? root : Path.Combine(root, project);
        var dataDir = FindChild(projectDir, DataFolder)
            ?? throw new RecipeException($"{root} has no {Prefix(project)}{DataFolder} folder.");
        var dataName = Path.GetFileName(dataDir);
        List<DataFile> files;
        try
        {
            files = [.. Directory.EnumerateFiles(dataDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(RpgMakerStrings.IsTextFile)
                .Order(StringComparer.Ordinal)
                .Select(name => new DataFile(name, $"{DataFolder}/{name}", $"{Prefix(project)}{dataName}/{name}"))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't list {Prefix(project)}{dataName}: {e.Message}", null, e);
        }

        return new Project(root, files, Prefix(project), match.Kind.Variant == EngineVariants.RpgMakerMz);
    }

    private static string Prefix(string project) => project.Length == 0 ? string.Empty : project + "/";

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

    /// <param name="Name">File name as on disk.</param>
    /// <param name="ProjectPath">Path from the project folder, the key's file part (<c>data/Map001.json</c>).</param>
    /// <param name="GamePath">Path from the game folder (<c>www/data/Map001.json</c> on MV).</param>
    private sealed record DataFile(string Name, string ProjectPath, string GamePath);

    /// <param name="Prefix">The project folder relative to the game folder, with a trailing <c>/</c> (empty at the root).</param>
    /// <param name="IsMz">RPG Maker MZ (else MV).</param>
    private sealed record Project(string GameDir, IReadOnlyList<DataFile> Files, string Prefix, bool IsMz)
    {
        public DataFile? Find(string projectPath) => Files.FirstOrDefault(f => f.ProjectPath == projectPath);

        /// <summary>A file of the project, relative to the game folder (the folder name as on disk when it exists).</summary>
        public string Path(string folder, string name)
        {
            var dir = System.IO.Path.Combine(GameDir, Prefix, folder);
            var actual = Directory.Exists(dir) ? folder : FindChild(System.IO.Path.Combine(GameDir, Prefix), folder) is { } found ? System.IO.Path.GetFileName(found) : folder;
            return $"{Prefix}{actual}/{name}";
        }
    }
}
