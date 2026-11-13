using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Recipes.Agent;

/// <summary>
/// The strings of a game the toolkit can't read (task 57), as the agent's own extractor wrote them into its workspace
/// (<c>agent/strings.json</c> of the job folder): <c>{strings: [{key, original, file?, context?, maxLength?,
/// placeholders?}]}</c>. It stands in for a recipe tool during generation (the MCP server's string tools, the
/// validator, the Translate stage) but never rebuilds: the agent's own rebuild script does, inside its sandbox, and the
/// pack is a binary patch (no pack names this tool, and no agent code runs anywhere else).
/// Markup = exactly the placeholders the extractor listed (each must occur in its original), so the validator checks
/// a translation keeps them; a string's placeholders are every occurrence of the listed ones in its original, in order.
/// </summary>
public sealed class AgentScriptTool : IRecipeTool
{
    /// <summary>The <c>tool</c> of <c>source.json</c> for strings the agent extracted.</summary>
    public const string ToolId = "agent-script";

    public const int MaxStrings = 200_000;

    public const int MaxKeyLength = 512;

    private const long MaxFileBytes = 256L << 20;

    private readonly string _stringsPath;
    private TextMarkup? _markup;
    private Dictionary<string, string> _fileOfKey = new(StringComparer.Ordinal);

    /// <param name="stringsPath">The extractor's output file (<c>agent/strings.json</c>).</param>
    public AgentScriptTool(string stringsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(stringsPath);
        _stringsPath = stringsPath;
    }

    public string Id => ToolId;

    public Version Version { get; } = new(1, 0, 0);

    public GameEngine Engine => GameEngine.Unknown;

    /// <summary>The listed placeholders (read on first use; none when the file can't be read).</summary>
    public TextMarkup Markup
    {
        get
        {
            if (_markup is null)
            {
                try
                {
                    _markup = MarkupOf(Read().Strings);
                }
                catch (RecipeException)
                {
                    _markup = TextMarkup.None;
                }
            }

            return _markup;
        }
    }

    /// <summary>Whether the extractor wrote its file.</summary>
    public bool Exists => File.Exists(_stringsPath);

    public RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default)
    {
        var extraction = Read();
        _markup = MarkupOf(extraction.Strings);
        return extraction;
    }

    public RebuildResult Rebuild(string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default) =>
        throw new RecipeException("Strings the agent extracted are rebuilt by the agent's own rebuild script in the Build stage, not by the toolkit.");

    public IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Read();
        return [.. keys
            .Select(k => _fileOfKey.GetValueOrDefault(k))
            .OfType<string>()
            .Where(file => File.Exists(Path.Combine(gameDir, file.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Reads and checks the file; every mistake is a <see cref="RecipeException"/> the agent can act on.</summary>
    /// <exception cref="RecipeException">Missing, unreadable, or a string breaks a rule.</exception>
    public RecipeExtraction Read()
    {
        byte[] data;
        try
        {
            var info = new FileInfo(_stringsPath);
            if (!info.Exists)
            {
                throw new RecipeException("strings.json isn't there: run the extractor so it writes strings.json in the workspace.");
            }

            if (info.Length > MaxFileBytes)
            {
                throw new RecipeException($"strings.json is larger than {MaxFileBytes >> 20} MB.");
            }

            data = File.ReadAllBytes(_stringsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"strings.json can't be read: {e.Message}", e);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { AllowTrailingCommas = true });
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new RecipeException($"strings.json is not valid JSON: {e.Message}", e);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("strings", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new RecipeException("strings.json must be an object with a \"strings\" array.");
        }

        var strings = new List<ExtractedString>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var fileOfKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var where = $"strings[{index++}]";
            if (strings.Count >= MaxStrings)
            {
                throw new RecipeException($"strings.json lists more than {MaxStrings} strings.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new RecipeException($"{where} is not an object.");
            }

            var key = Text(item, "key", where);
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key.Any(char.IsControl))
            {
                throw new RecipeException($"{where}: \"key\" must be a non-empty string of at most {MaxKeyLength} characters without control characters.");
            }

            if (!keys.Add(key))
            {
                throw new RecipeException($"{where}: the key \"{key}\" is listed twice; keys must be unique and the same on every run.");
            }

            var original = Text(item, "original", where);
            if (string.IsNullOrEmpty(original))
            {
                throw new RecipeException($"{where} (\"{key}\"): \"original\" must be the text as the game shows it, not empty.");
            }

            var file = Text(item, "file", where);
            if (file is not null)
            {
                if (!IsRelativePath(file))
                {
                    throw new RecipeException($"{where} (\"{key}\"): \"file\" must be the game file's path relative to the game folder, with / between folders.");
                }

                files.Add(file);
                fileOfKey[key] = file;
            }

            int? maxLength = null;
            if (item.TryGetProperty("maxLength", out var max) && max.ValueKind != JsonValueKind.Null)
            {
                if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out var value) || value < 1)
                {
                    throw new RecipeException($"{where} (\"{key}\"): \"maxLength\" must be a positive whole number of characters.");
                }

                maxLength = value;
            }

            var placeholders = new List<string>();
            if (item.TryGetProperty("placeholders", out var tokens) && tokens.ValueKind != JsonValueKind.Null)
            {
                if (tokens.ValueKind != JsonValueKind.Array)
                {
                    throw new RecipeException($"{where} (\"{key}\"): \"placeholders\" must be an array of strings.");
                }

                foreach (var token in tokens.EnumerateArray())
                {
                    if (token.ValueKind != JsonValueKind.String || token.GetString() is not { Length: > 0 } value || !original.Contains(value, StringComparison.Ordinal))
                    {
                        throw new RecipeException($"{where} (\"{key}\"): every placeholder must be a non-empty string found in the original as written.");
                    }

                    placeholders.Add(value);
                }
            }

            strings.Add(new ExtractedString(key, original, Text(item, "context", where), maxLength, Occurrences(original, placeholders)));
        }

        strings.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        _fileOfKey = fileOfKey;
        return new RecipeExtraction(ToolId, Version, strings, [.. files]);
    }

    /// <summary>A relative, <c>/</c>-separated path without <c>.</c> / <c>..</c> parts, drives or backslashes.</summary>
    public static bool IsRelativePath(string path) =>
        path.Length > 0 && path.IndexOfAny(['\\', ':']) < 0 && !path.Any(char.IsControl)
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static string? Text(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new RecipeException($"{where}: \"{name}\" must be a string.");
    }

    /// <summary>
    /// Every occurrence of the listed placeholders in <paramref name="original"/>, in order: extractors often list a
    /// token once though the original has it twice (<c>%s waves to %s!</c>), and the validator counts occurrences.
    /// </summary>
    private static List<string> Occurrences(string original, List<string> listed)
    {
        if (listed.Count == 0)
        {
            return listed;
        }

        var pattern = string.Join('|', listed.Distinct(StringComparer.Ordinal)
            .OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).Select(Regex.Escape));
        return [.. Regex.Matches(original, pattern, RegexOptions.CultureInvariant).Select(m => m.Value)];
    }

    /// <summary>Every listed placeholder as a literal, longest first, so a longer token isn't matched as a shorter one.</summary>
    public static TextMarkup MarkupOf(IEnumerable<ExtractedString> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var tokens = strings.SelectMany(s => s.Placeholders).Distinct(StringComparer.Ordinal)
            .OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).ToList();
        return tokens.Count == 0
            ? TextMarkup.None
            : new TextMarkup(new Regex(string.Join('|', tokens.Select(Regex.Escape)), RegexOptions.CultureInvariant), TagBrackets.None);
    }
}
