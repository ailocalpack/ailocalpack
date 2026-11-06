using System.Text.Json;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Recipes.RpgMaker;

/// <summary>
/// Recipe tool <c>rpgmaker</c> for RPG Maker MV (<c>www/data</c>) and MZ (<c>data</c>), macOS bundles included
/// (<c>Contents/Resources/app.nw</c>). Text lives in the JSON files of the <c>data</c> folder (see
/// <see cref="RpgMakerStrings"/>). Key = <c>&lt;path from the project folder&gt;:&lt;JSON Pointer&gt;</c>, e.g.
/// <c>data/Map001.json:/events/1/pages/0/list/1/parameters/0</c>: the project folder is the one holding <c>js/</c> and
/// <c>data/</c>, so the same game has the same keys in its Windows, Linux and macOS builds. Rebuilding replaces only
/// the translated string values, keeping the rest of each file byte for byte.
/// </summary>
public sealed class RpgMakerRecipe : IRecipeTool
{
    public const string ToolId = "rpgmaker";

    private const string DataFolder = "data";

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

    public RebuildResult Rebuild(string gameDir, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default)
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

        return new Project(root, files);
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

    private sealed record Project(string GameDir, IReadOnlyList<DataFile> Files)
    {
        public DataFile? Find(string projectPath) => Files.FirstOrDefault(f => f.ProjectPath == projectPath);
    }
}
