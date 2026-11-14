using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Games;

/// <summary>
/// The languages each game ships with (task 80), remembered in <see cref="AppPaths.OfficialLanguagesFile"/> so the library
/// knows them at start and offline, and the store isn't asked again for a while. A cache: a missing, broken or newer-format
/// file starts empty (and a newer one is never overwritten). Thread-safe; never throws for disk state.
/// </summary>
public sealed class OfficialLanguageStore
{
    private const int SchemaVersion = 1;

    private readonly string _file;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private Dictionary<GameId, OfficialLanguages>? _games;
    private bool _readOnly;

    public OfficialLanguageStore(AppPaths paths, ILogger<OfficialLanguageStore>? logger = null)
        : this(paths.OfficialLanguagesFile, logger)
    {
    }

    public OfficialLanguageStore(string file, ILogger<OfficialLanguageStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _file = file;
        _logger = logger ?? NullLogger<OfficialLanguageStore>.Instance;
    }

    /// <summary>The remembered languages of <paramref name="game"/>; <c>null</c> when never read.</summary>
    public OfficialLanguages? Find(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_lock)
        {
            return Load().GetValueOrDefault(game);
        }
    }

    /// <summary>
    /// Remembers the languages of several games and saves once. Returns the games whose languages changed; the same
    /// languages read again from the client's cache keep the earlier entry (nothing is saved), from the store they renew
    /// its check time.
    /// </summary>
    public IReadOnlyList<GameId> Set(IEnumerable<KeyValuePair<GameId, OfficialLanguages>> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        lock (_lock)
        {
            var known = Load();
            var changed = new List<GameId>();
            var dirty = false;
            foreach (var (game, languages) in games)
            {
                var before = known.GetValueOrDefault(game);
                if (languages.SameAs(before))
                {
                    // A store answer's time says when to ask again; the client's cache is read every scan anyway.
                    if (languages.Source == OfficialLanguagesSource.SteamStore && languages.Checked != before!.Checked)
                    {
                        known[game] = languages;
                        dirty = true;
                    }

                    continue;
                }

                known[game] = languages;
                dirty = true;
                changed.Add(game);
            }

            if (dirty)
            {
                Save(known);
            }

            return changed;
        }
    }

    /// <summary>Forgets games no longer in the library (saves only when something was dropped).</summary>
    public void Keep(IReadOnlyCollection<GameId> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        lock (_lock)
        {
            var known = Load();
            var gone = known.Keys.Where(g => !games.Contains(g)).ToList();
            foreach (var game in gone)
            {
                known.Remove(game);
            }

            if (gone.Count > 0)
            {
                Save(known);
            }
        }
    }

    private Dictionary<GameId, OfficialLanguages> Load()
    {
        if (_games is not null)
        {
            return _games;
        }

        _games = [];
        try
        {
            if (!File.Exists(_file))
            {
                return _games;
            }

            var json = JsonSerializer.Deserialize(File.ReadAllBytes(_file), OfficialLanguagesJsonContext.Default.OfficialLanguagesFileJson);
            if (json?.SchemaVersion is > SchemaVersion)
            {
                _readOnly = true;
                _logger.LogWarning("{File} is from a newer app (schema {Version}); not using it", _file, json.SchemaVersion);
                return _games;
            }

            foreach (var entry in json?.Games ?? [])
            {
                if (FromJson(entry) is { } read)
                {
                    _games[read.Game] = read.Languages;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(e, "Can't read {File}; reading languages again", _file);
        }

        return _games;
    }

    private void Save(Dictionary<GameId, OfficialLanguages> games)
    {
        if (_readOnly)
        {
            return;
        }

        var json = new OfficialLanguagesFileJson(
            SchemaVersion,
            [.. games.OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => ToJson(g.Key, g.Value))]);
        try
        {
            AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(json, OfficialLanguagesJsonContext.Default.OfficialLanguagesFileJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache: kept in memory, read again next start.
            _logger.LogWarning(e, "Can't save {File}", _file);
        }
    }

    private static OfficialGameJson ToJson(GameId game, OfficialLanguages languages) => new(
        game.ToString(),
        languages.Source == OfficialLanguagesSource.SteamClient ? "steam-client" : "steam-store",
        languages.Checked,
        languages.SteamLanguage,
        [.. languages.Languages.Select(l => new OfficialLanguageJson(l.Tag, l.Interface, l.Subtitles, l.FullAudio))]);

    private static (GameId Game, OfficialLanguages Languages)? FromJson(OfficialGameJson? entry)
    {
        if (entry is null || !GameId.TryParse(entry.Game, out var game) || entry.Checked is not { } checkedAt)
        {
            return null;
        }

        OfficialLanguagesSource? source = entry.Source switch
        {
            "steam-client" => OfficialLanguagesSource.SteamClient,
            "steam-store" => OfficialLanguagesSource.SteamStore,
            _ => null,
        };
        if (source is null)
        {
            return null;
        }

        var languages = new List<OfficialLanguage>();
        foreach (var language in entry.Languages ?? [])
        {
            if (GameLanguages.Canonical(language?.Tag) is { } tag)
            {
                languages.Add(new OfficialLanguage(tag, language!.Interface, language.Subtitles, language.FullAudio));
            }
        }

        var steamLanguage = GameLanguages.Canonical(entry.SteamLanguage);
        return (game, new OfficialLanguages(languages, source.Value, checkedAt, steamLanguage));
    }
}

internal sealed record OfficialLanguagesFileJson(int? SchemaVersion, List<OfficialGameJson?>? Games);

internal sealed record OfficialGameJson(string? Game, string? Source, DateTimeOffset? Checked, string? SteamLanguage, List<OfficialLanguageJson?>? Languages);

internal sealed record OfficialLanguageJson(string? Tag, bool Interface, bool Subtitles, bool FullAudio);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OfficialLanguagesFileJson))]
internal sealed partial class OfficialLanguagesJsonContext : JsonSerializerContext;
