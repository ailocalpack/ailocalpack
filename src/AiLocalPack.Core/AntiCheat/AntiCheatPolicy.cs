using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.AntiCheat;

/// <summary>One anti-cheat the toolkit can detect.</summary>
/// <param name="Id">Stable ID stored in <see cref="Game.AntiCheat"/> (<c>eac</c>, <c>battleye</c>…).</param>
/// <param name="Name">Display name (product name, not translated).</param>
/// <param name="Markers">
/// File or folder markers, checked in every folder of the game tree: a name (<c>BEClient_x64.dll</c>), a folder with a
/// trailing <c>/</c> (<c>EasyAntiCheat/</c>) or a short path (<c>pb/pbcl.dll</c>). The last part may use <c>*</c> and
/// <c>?</c> wildcards. Names ignore case.
/// </param>
public sealed record AntiCheatRule(string Id, string Name, IReadOnlyList<string> Markers);

/// <summary>What the policy says about one game (<see cref="AntiCheatPolicy.Resolve"/>).</summary>
/// <param name="AntiCheatId">Anti-cheat found in the files or listed for the game ID; <c>null</c> = none.</param>
/// <param name="Name">Display name of that anti-cheat (its ID when the policy does not know it).</param>
/// <param name="IsAllowed">The game is whitelisted (officially allows mods): the anti-cheat does not block it.</param>
public sealed record AntiCheatVerdict(string? AntiCheatId, string? Name, bool IsAllowed)
{
    public static AntiCheatVerdict None { get; } = new(null, null, false);

    /// <summary>Install and generation must refuse the game.</summary>
    public bool IsBlocked => AntiCheatId is not null && !IsAllowed;
}

/// <summary>
/// Anti-cheat rules and per-game overrides: the list bundled with the app (<see cref="Bundled"/>, <c>anti-cheat.json</c>)
/// and, later, the registry's <c>policy/anti-cheat.json</c> in the same format. Holds the detection rules, games known to
/// have an anti-cheat by ID (kernel anti-cheats installed outside the game folder) and the whitelist of games that
/// officially allow mods, which beats both.
/// </summary>
public sealed partial class AntiCheatPolicy
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Value of <see cref="Game.AntiCheat"/> after a check that found nothing.</summary>
    public const string NoneId = "none";

    private static readonly Lazy<AntiCheatPolicy> BundledPolicy = new(LoadBundled);

    private readonly Dictionary<string, AntiCheatRule> _byId;

    public AntiCheatPolicy(
        IReadOnlyList<AntiCheatRule> antiCheats,
        IReadOnlyDictionary<GameId, string> knownGames,
        IReadOnlySet<GameId> allowedGames)
    {
        ArgumentNullException.ThrowIfNull(antiCheats);
        ArgumentNullException.ThrowIfNull(knownGames);
        ArgumentNullException.ThrowIfNull(allowedGames);
        AntiCheats = antiCheats;
        KnownGames = knownGames;
        AllowedGames = allowedGames;
        _byId = antiCheats.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    /// <summary>The list shipped inside the app.</summary>
    public static AntiCheatPolicy Bundled => BundledPolicy.Value;

    /// <summary>Detection rules, in the order they are reported.</summary>
    public IReadOnlyList<AntiCheatRule> AntiCheats { get; }

    /// <summary>Games known to use an anti-cheat that leaves no files in the game folder: game ID → anti-cheat ID.</summary>
    public IReadOnlyDictionary<GameId, string> KnownGames { get; }

    /// <summary>Games that officially allow mods: never blocked.</summary>
    public IReadOnlySet<GameId> AllowedGames { get; }

    public AntiCheatRule? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Display name of an anti-cheat ID; the ID itself when unknown (stored by a newer list).</summary>
    public string DisplayName(string id) => Find(id)?.Name ?? id;

    /// <summary>
    /// Combines what the files showed (<paramref name="detectedId"/>: an anti-cheat ID, <see cref="NoneId"/> or <c>null</c>
    /// = not checked) with the per-game lists. The known-games list fills in when the files showed nothing.
    /// </summary>
    public AntiCheatVerdict Resolve(GameId gameId, string? detectedId)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        var id = detectedId is null or NoneId ? KnownGames.GetValueOrDefault(gameId) : detectedId;
        return id is null ? AntiCheatVerdict.None : new AntiCheatVerdict(id, DisplayName(id), AllowedGames.Contains(gameId));
    }

    /// <inheritdoc cref="Resolve(GameId, string?)"/>
    public AntiCheatVerdict Resolve(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Resolve(game.Id, game.AntiCheat);
    }

    /// <summary>Reads a policy file. Throws <see cref="AntiCheatPolicyException"/> on invalid JSON or rules.</summary>
    public static AntiCheatPolicy Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AntiCheatPolicyFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, AntiCheatPolicyJsonContext.Default.AntiCheatPolicyFile);
        }
        catch (JsonException e)
        {
            throw new AntiCheatPolicyException($"Invalid JSON: {e.Message}", e);
        }

        if (file is null)
        {
            throw new AntiCheatPolicyException("The file is empty.");
        }

        if (file.SchemaVersion != CurrentSchemaVersion)
        {
            throw new AntiCheatPolicyException($"Unsupported schemaVersion {file.SchemaVersion}.");
        }

        var rules = new List<AntiCheatRule>();
        foreach (var entry in file.AntiCheats ?? [])
        {
            rules.Add(ReadRule(entry));
        }

        var duplicate = rules.GroupBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new AntiCheatPolicyException($"Anti-cheat '{duplicate.Key}' is listed twice.");
        }

        var ids = rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var known = new Dictionary<GameId, string>();
        foreach (var entry in file.Games ?? [])
        {
            var game = ReadGameId(entry?.Game);
            var antiCheat = entry!.AntiCheat;
            if (antiCheat is null || !ids.Contains(antiCheat))
            {
                throw new AntiCheatPolicyException($"Game {game}: unknown anti-cheat '{antiCheat}'.");
            }

            if (!known.TryAdd(game, antiCheat))
            {
                throw new AntiCheatPolicyException($"Game {game} is listed twice in 'games'.");
            }
        }

        var allowed = new HashSet<GameId>();
        foreach (var entry in file.Allowed ?? [])
        {
            allowed.Add(ReadGameId(entry?.Game));
        }

        return new AntiCheatPolicy(rules, known, allowed);
    }

    private static AntiCheatRule ReadRule(AntiCheatJson? entry)
    {
        if (entry?.Id is not { } id || !IdPattern().IsMatch(id) || id == NoneId)
        {
            throw new AntiCheatPolicyException($"Invalid anti-cheat ID '{entry?.Id}' (lowercase letters, digits, '-').");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new AntiCheatPolicyException($"Anti-cheat '{id}' has no name.");
        }

        if (entry.Markers is not { Count: > 0 } markers)
        {
            throw new AntiCheatPolicyException($"Anti-cheat '{id}' has no markers.");
        }

        foreach (var marker in markers)
        {
            if (!IsValidMarker(marker))
            {
                throw new AntiCheatPolicyException($"Anti-cheat '{id}': invalid marker '{marker}'.");
            }
        }

        return new AntiCheatRule(id, entry.Name, [.. markers.Select(m => m!)]);
    }

    /// <summary>Relative, '/'-separated, no empty / dot parts, wildcards only in the last part.</summary>
    private static bool IsValidMarker(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = (marker.EndsWith('/') ? marker[..^1] : marker).Split('/');
        return parts.All(p => p.Length > 0 && p != "." && p != "..")
            && parts[..^1].All(p => p.IndexOfAny(['*', '?']) < 0);
    }

    private static GameId ReadGameId(string? text) =>
        GameId.TryParse(text, out var id) ? id : throw new AntiCheatPolicyException($"Invalid game ID '{text}'.");

    private static AntiCheatPolicy LoadBundled()
    {
        using var stream = typeof(AntiCheatPolicy).Assembly.GetManifestResourceStream(BundledResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {BundledResourceName}.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal const string BundledResourceName = "AiLocalPack.Core.AntiCheat.anti-cheat.json";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}

/// <summary>An anti-cheat policy file that cannot be used.</summary>
public sealed class AntiCheatPolicyException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record AntiCheatPolicyFile(
    int SchemaVersion,
    IReadOnlyList<AntiCheatJson?>? AntiCheats,
    IReadOnlyList<AntiCheatGameJson?>? Games,
    IReadOnlyList<AllowedGameJson?>? Allowed);

internal sealed record AntiCheatJson(string? Id, string? Name, IReadOnlyList<string?>? Markers);

internal sealed record AntiCheatGameJson(string? Game, string? AntiCheat);

/// <param name="Note">Why the game is allowed (link to the studio's statement); for maintainers, not shown.</param>
internal sealed record AllowedGameJson(string? Game, string? Note);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AntiCheatPolicyFile))]
internal sealed partial class AntiCheatPolicyJsonContext : JsonSerializerContext;
