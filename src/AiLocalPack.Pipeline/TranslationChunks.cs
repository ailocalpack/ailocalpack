using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline;

/// <summary>One chunk of the Translate stage: the keys the agent translates in one run, in the game's order.</summary>
public sealed record TranslationChunk(IReadOnlyList<string> Keys);

/// <summary>How big a chunk may get.</summary>
/// <param name="MaxStrings">Strings per chunk at most (<see cref="GenerationEstimateModel.StringsPerChunk"/>).</param>
/// <param name="MaxTokens">Estimated prompt tokens of the chunk's strings at most (<see cref="ChunkPlanner.Tokens"/>); a single larger string gets a chunk of its own.</param>
public sealed record ChunkBudget(int MaxStrings, int MaxTokens)
{
    /// <summary>
    /// 50 strings or ~4 000 tokens of originals and context notes. A guess to tune with real jobs: bigger chunks share
    /// the fixed prompt and glossary cost (<see cref="GenerationEstimateModel"/>), smaller ones lose less on a retry.
    /// </summary>
    public static ChunkBudget Default { get; } = new(GenerationEstimateModel.StringsPerChunk, 4_000);
}

/// <summary>
/// Cuts the extracted strings into the Translate stage's chunks (task 50), once, at the Extract stage, so a resumed job
/// goes on with the same chunks. Strings are put in the game's order (keys compared with their numbers as numbers, so
/// <c>…/list/2</c> comes before <c>…/list/10</c>) and grouped by the file or scene they come from
/// (<see cref="Group"/>): a chunk ends at a group boundary when the next group doesn't fit in it and it is at least half
/// full; a group larger than a chunk is cut into consecutive pieces. Each chunk stays within the
/// <see cref="ChunkBudget"/>'s strings and tokens.
/// </summary>
public static class ChunkPlanner
{
    /// <summary>Tokens a string costs in a prompt besides its text (key, JSON, placeholders).</summary>
    private const int TokensPerString = 20;

    public static IReadOnlyList<TranslationChunk> Plan(string tool, IReadOnlyList<ExtractedString> strings, ChunkBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(strings);
        budget ??= ChunkBudget.Default;
        if (budget.MaxStrings < 1 || budget.MaxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "A chunk must hold at least one string and one token.");
        }

        var ordered = strings.OrderBy(s => s.Key, NaturalKeyComparer.Instance).ToList();
        var groups = new List<List<ExtractedString>>();
        string? current = null;
        foreach (var s in ordered)
        {
            var group = Group(tool, s.Key);
            if (groups.Count == 0 || group != current)
            {
                groups.Add([]);
                current = group;
            }

            groups[^1].Add(s);
        }

        var chunks = new List<TranslationChunk>();
        var keys = new List<string>();
        var tokens = 0;
        foreach (var group in groups)
        {
            var groupTokens = group.Sum(Tokens);
            var fits = keys.Count + group.Count <= budget.MaxStrings && tokens + groupTokens <= budget.MaxTokens;
            var halfFull = keys.Count * 2 >= budget.MaxStrings || tokens * 2 >= budget.MaxTokens;
            if (keys.Count > 0 && !fits && halfFull)
            {
                Close();
            }

            foreach (var s in group)
            {
                var cost = Tokens(s);
                if (keys.Count > 0 && (keys.Count + 1 > budget.MaxStrings || tokens + cost > budget.MaxTokens))
                {
                    Close();
                }

                keys.Add(s.Key);
                tokens += cost;
            }
        }

        if (keys.Count > 0)
        {
            Close();
        }

        return chunks;

        void Close()
        {
            chunks.Add(new TranslationChunk([.. keys]));
            keys.Clear();
            tokens = 0;
        }
    }

    /// <summary>Estimated prompt tokens of a string: its original and context at ~4 characters a token, plus its JSON.</summary>
    public static int Tokens(ExtractedString s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return ((s.Original.Length + (s.Context?.Length ?? 0) + 3) / 4) + TokensPerString;
    }

    /// <summary>
    /// The file or scene a key comes from, by its tool's key format (<see cref="IRecipeTool.Extract"/>): RPG Maker and
    /// Unity = the file (<c>data/Map001.json</c>), Ren'Py dialogue = its label (<c>say:start_1a2b3c4d</c> → <c>say:start</c>)
    /// and string translations one group, Godot scene texts = the scene, translation keys one group, Unreal = resource +
    /// namespace; another tool's keys = everything before the last <c>:</c>.
    /// </summary>
    public static string Group(string tool, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        switch (tool)
        {
            case "rpgmaker" or "unity":
                return Before(key, key.IndexOf(':', StringComparison.Ordinal));
            case "renpy" when key.StartsWith("say:", StringComparison.Ordinal):
                return RenPyLabel(key);
            case "renpy":
                return "string";
            case "godot" when key.StartsWith("scene:", StringComparison.Ordinal):
                return Before(key, key.IndexOf(':', "scene:".Length));
            case "godot":
                return "tr";
            case "unreal":
                var resource = key.IndexOf(':', "locres:".Length);
                return resource < 0 ? key : Before(key, key.IndexOf(':', resource + 1));
            default:
                return Before(key, key.LastIndexOf(':'));
        }
    }

    private static string Before(string key, int index) => index < 0 ? key : key[..index];

    /// <summary><c>say:&lt;label&gt;_&lt;8 hex&gt;[_n]</c> → <c>say:&lt;label&gt;</c>.</summary>
    private static string RenPyLabel(string key)
    {
        var parts = key.Split('_');
        var end = parts.Length;
        if (end > 1 && parts[end - 1].All(char.IsAsciiDigit) && parts[end - 1].Length > 0 && !IsHash(parts[end - 1]))
        {
            end--;
        }

        if (end > 1 && IsHash(parts[end - 1]))
        {
            end--;
        }

        return string.Join('_', parts[..end]);
    }

    private static bool IsHash(string part) => part.Length == 8 && part.All(char.IsAsciiHexDigit);
}

/// <summary>Compares keys ordinally, except that runs of digits compare as numbers (<c>a2</c> &lt; <c>a10</c>).</summary>
public sealed class NaturalKeyComparer : IComparer<string>
{
    public static NaturalKeyComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var byNumber = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                // Same number: fewer leading zeros first, so the order stays total.
                var byWidth = (i - si).CompareTo(j - sj);
                if (byWidth != 0)
                {
                    return byWidth;
                }

                continue;
            }

            if (x[i] != y[j])
            {
                return x[i].CompareTo(y[j]);
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>
/// <c>chunks.json</c> in the job folder (<see cref="GenerationFiles.ChunksPath"/>): <c>{schemaVersion: 1, chunks:
/// [{keys: [key]}]}</c>, written atomically by the Extract stage, read by every Translate step.
/// </summary>
public static class ChunkPlanFile
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new(ChunkJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <exception cref="IOException">The file can't be written.</exception>
    public static void Write(GenerationFiles files, IReadOnlyList<TranslationChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(chunks);
        var json = new ChunkFileJson(CurrentSchemaVersion, [.. chunks.Select(c => new ChunkJson(c.Keys))]);
        AtomicFile.WriteAllBytes(files.ChunksPath, JsonSerializer.SerializeToUtf8Bytes(json, WriteOptions.GetTypeInfo(typeof(ChunkFileJson))));
    }

    /// <summary>The chunks, or <c>null</c> when the Extract stage hasn't written them.</summary>
    /// <exception cref="GenerationFileException">The file can't be read or isn't valid.</exception>
    public static IReadOnlyList<TranslationChunk>? Read(GenerationFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var path = files.ChunksPath;
        byte[] data;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GenerationFileException(path, e.Message, e);
        }

        ChunkFileJson? json;
        try
        {
            json = JsonSerializer.Deserialize(data, ChunkJsonContext.Default.ChunkFileJson);
        }
        catch (JsonException e)
        {
            throw new GenerationFileException(path, $"not valid JSON ({e.Message})", e);
        }

        if (json is null || json.SchemaVersion != CurrentSchemaVersion)
        {
            throw new GenerationFileException(path, $"schemaVersion {json?.SchemaVersion} is not supported");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chunks = new List<TranslationChunk>();
        foreach (var chunk in json.Chunks ?? [])
        {
            if (chunk?.Keys is not { Count: > 0 } keys || keys.Any(k => string.IsNullOrEmpty(k) || !seen.Add(k)))
            {
                throw new GenerationFileException(path, $"chunk {chunks.Count + 1} is empty, has an empty key or repeats a key");
            }

            chunks.Add(new TranslationChunk([.. keys]));
        }

        return chunks;
    }
}

internal sealed record ChunkFileJson(int SchemaVersion, IReadOnlyList<ChunkJson?>? Chunks);

internal sealed record ChunkJson(IReadOnlyList<string>? Keys);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChunkFileJson))]
internal sealed partial class ChunkJsonContext : JsonSerializerContext;
