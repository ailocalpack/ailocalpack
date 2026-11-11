using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;

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
/// and the registry's <c>policy/anti-cheat.json</c> in the same format, published in the index (<see cref="WithRegistry"/>). Holds the detection rules, games known to
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
    private readonly ConditionalWeakTable<RegistryIndex, AntiCheatPolicy> _merged = new();
    private string? _stamp;

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

    /// <summary>
    /// The policy with the registry's on top (<see cref="RegistryIndex.AntiCheats"/>, the games' anti-cheat and allowed
    /// flags): a registry rule replaces the bundled one with its ID and new ones are added after them, a game the registry
    /// lists takes its anti-cheat, the allowed lists add up. Without an index (none fetched or saved) this policy as is.
    /// The result is kept per index, so callers may ask for every game.
    /// </summary>
    public AntiCheatPolicy WithRegistry(RegistryIndex? index)
    {
        if (index is null)
        {
            return this;
        }

        return _merged.GetValue(index, i => Merge(i.AntiCheats, i.Games.Values.Where(g => g.AntiCheat is not null).ToDictionary(g => g.Id, g => g.AntiCheat!), i.Games.Values.Where(g => g.AntiCheatAllowed).Select(g => g.Id)));
    }

    private AntiCheatPolicy Merge(IReadOnlyList<AntiCheatRule> rules, IReadOnlyDictionary<GameId, string> known, IEnumerable<GameId> allowed)
    {
        var overrides = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var merged = AntiCheats.Select(r => overrides.GetValueOrDefault(r.Id) ?? r).ToList();
        merged.AddRange(rules.Where(r => Find(r.Id) is null));
        var games = new Dictionary<GameId, string>(KnownGames);
        foreach (var (game, antiCheat) in known)
        {
            games[game] = antiCheat;
        }

        return new AntiCheatPolicy(merged, games, AllowedGames.Concat(allowed).ToHashSet());
    }

    /// <summary>
    /// A short fingerprint of the detection rules (IDs and markers, in order): a game checked under other rules is
    /// checked again, since a new marker may find an anti-cheat in it.
    /// </summary>
    public string RulesStamp => _stamp ??= Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", AntiCheats.Select(r => r.Id + "\t" + string.Join("\t", r.Markers))))))[..16];

    /// <summary>
    /// Reads a policy file. Throws <see cref="AntiCheatPolicyException"/> on invalid JSON or rules. The <c>games</c> list
    /// may name anti-cheats of <paramref name="baseline"/> too (the registry's file naming a bundled one).
    /// </summary>
    public static AntiCheatPolicy Parse(string json, AntiCheatPolicy? baseline = null)
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

        var ids = rules.Select(r => r.Id).Concat(baseline?.AntiCheats.Select(r => r.Id) ?? []).ToHashSet(StringComparer.Ordinal);
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

    private static AntiCheatRule ReadRule(AntiCheatJson? entry) =>
        TryCreateRule(entry?.Id, entry?.Name, entry?.Markers, out var rule, out var error)
            ? rule
            : throw new AntiCheatPolicyException(error);

    /// <summary>
    /// Checks one rule as <see cref="Parse"/> does (ID pattern, not <see cref="NoneId"/>, a name, valid markers); the
    /// registry index carries rules in the same shape.
    /// </summary>
    public static bool TryCreateRule(string? id, string? name, IReadOnlyList<string?>? markers, out AntiCheatRule rule, out string error)
    {
        rule = null!;
        if (!IsValidId(id))
        {
            error = $"Invalid anti-cheat ID '{id}' (lowercase letters, digits, '-').";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"Anti-cheat '{id}' has no name.";
            return false;
        }

        if (markers is not { Count: > 0 })
        {
            error = $"Anti-cheat '{id}' has no markers.";
            return false;
        }

        foreach (var marker in markers)
        {
            if (!IsValidMarker(marker))
            {
                error = $"Anti-cheat '{id}': invalid marker '{marker}'.";
                return false;
            }
        }

        error = "";
        rule = new AntiCheatRule(id!, name, [.. markers.Select(m => m!)]);
        return true;
    }

    /// <summary>An anti-cheat ID: lowercase letters, digits and dashes, not <see cref="NoneId"/>.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id) && id != NoneId;

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
