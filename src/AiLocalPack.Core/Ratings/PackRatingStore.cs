using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Ratings;

/// <summary>
/// What this player did with one installed registry pack (task 63): when they played with it, "Later" on the rating
/// prompt, their 👍 and their report. <see cref="Revision"/> is the revision played; installing another revision and
/// playing it starts the record over (the 👍 stays: it is for the pack's Discussion, not a revision).
/// </summary>
/// <param name="TriedAt">The first game session with <see cref="Revision"/> installed ended.</param>
/// <param name="PlayedAt">The latest such session ended.</param>
/// <param name="LaterAt">"Later" on the prompt (the prompt comes back after the next session).</param>
/// <param name="LaterCount">How many times "Later" was pressed for this revision.</param>
/// <param name="ReportedAt">The report of <see cref="Revision"/> was sent.</param>
/// <param name="ReportUrl">The report comment on GitHub.</param>
public sealed record PackRating(
    GameId Game,
    string Language,
    string PackId,
    int Revision,
    DateTimeOffset? TriedAt = null,
    DateTimeOffset? PlayedAt = null,
    DateTimeOffset? LaterAt = null,
    int LaterCount = 0,
    bool Liked = false,
    PackVerdict? Reported = null,
    DateTimeOffset? ReportedAt = null,
    Uri? ReportUrl = null)
{
    public bool IsTried => TriedAt is not null;

    public bool IsFor(GameId game, string language, string packId) => Game == game && Language == language && PackId == packId;
}

/// <summary>
/// <c>&lt;data&gt;/pack-ratings.json</c>: <see cref="PackRating"/> per game + language + pack. Kept in memory after the
/// first read; atomic writes; a damaged file is moved aside as <c>.&lt;utc&gt;.bak</c> and read as empty; a newer-format
/// file is read as empty and never overwritten. Thread-safe; never throws for disk state.
/// </summary>
public sealed class PackRatingStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private List<PackRating>? _ratings;
    private bool _newerFormat;

    public PackRatingStore(AppPaths paths, TimeProvider? time = null, ILogger<PackRatingStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = paths.PackRatingsFile;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>A record changed (any thread): the game's pages follow.</summary>
    public event EventHandler<GameId>? Changed;

    public PackRating? Find(GameId game, string language, string packId)
    {
        lock (_lock)
        {
            return Ratings().FirstOrDefault(r => r.IsFor(game, language, packId));
        }
    }

    /// <summary>A game session with <paramref name="revision"/> installed ended at <paramref name="at"/>.</summary>
    public PackRating Played(GameId game, string language, string packId, int revision, DateTimeOffset at) =>
        Change(game, language, packId, revision, r => r with { TriedAt = r.TriedAt ?? at, PlayedAt = r.PlayedAt is { } last && last > at ? last : at });

    /// <summary>"Later" on the rating prompt.</summary>
    public PackRating Later(GameId game, string language, string packId, int revision, DateTimeOffset at) =>
        Change(game, language, packId, revision, r => r with { LaterAt = at, LaterCount = r.LaterCount + 1 });

    /// <summary>The player's 👍 as GitHub has it now.</summary>
    public PackRating SetLiked(GameId game, string language, string packId, int revision, bool liked) =>
        Change(game, language, packId, revision, r => r with { Liked = liked });

    /// <summary>A report of <paramref name="revision"/> was sent.</summary>
    public PackRating Reported(GameId game, string language, string packId, int revision, PackVerdict verdict, DateTimeOffset at, Uri? url) =>
        Change(game, language, packId, revision, r => r with { Reported = verdict, ReportedAt = at, ReportUrl = url ?? r.ReportUrl });

    /// <summary>
    /// Applies <paramref name="change"/> to the record of the pack, made for <paramref name="revision"/> (another
    /// revision's record starts over, keeping the 👍), and saves.
    /// </summary>
    private PackRating Change(GameId game, string language, string packId, int revision, Func<PackRating, PackRating> change)
    {
        ArgumentNullException.ThrowIfNull(game);
        PackRating updated;
        lock (_lock)
        {
            var ratings = Ratings();
            var index = ratings.FindIndex(r => r.IsFor(game, language, packId));
            var current = index >= 0 ? ratings[index] : null;
            var start = current is null ? new PackRating(game, language, packId, revision)
                : current.Revision == revision ? current
                : new PackRating(game, language, packId, revision, Liked: current.Liked);
            updated = change(start);
            if (updated == current)
            {
                return updated;
            }

            if (index >= 0)
            {
                ratings[index] = updated;
            }
            else
            {
                ratings.Add(updated);
            }

            Save(ratings);
        }

        Changed?.Invoke(this, game);
        return updated;
    }

    private List<PackRating> Ratings() => _ratings ??= Load();

    private List<PackRating> Load()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(_file))
            {
                return [];
            }

            bytes = File.ReadAllBytes(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't read {File}", _file);
            return [];
        }

        PackRatingsJson? json;
        try
        {
            json = JsonSerializer.Deserialize(bytes, PackRatingJsonContext.Default.PackRatingsJson);
        }
        catch (JsonException e)
        {
            MoveAside(e);
            return [];
        }

        if (json is null)
        {
            MoveAside(null);
            return [];
        }

        if (json.SchemaVersion > CurrentSchemaVersion)
        {
            _logger.LogWarning("{File} has schema {Version}, newer than this app; read as empty and left as it is", _file, json.SchemaVersion);
            _newerFormat = true;
            return [];
        }

        var list = new List<PackRating>();
        foreach (var entry in json.Packs ?? [])
        {
            if (entry is null
                || !GameId.TryParse(entry.Game, out var game)
                || entry.Language is not { Length: > 0 } language
                || entry.Pack is not { Length: > 0 } pack
                || entry.Revision <= 0
                || list.Any(r => r.IsFor(game, language, pack)))
            {
                _logger.LogWarning("Skipping a damaged entry of {File}", _file);
                continue;
            }

            PackVerdict? verdict = entry.Reported switch
            {
                "works" => PackVerdict.Works,
                "doesNotWork" => PackVerdict.DoesNotWork,
                _ => null,
            };
            Uri.TryCreate(entry.ReportUrl, UriKind.Absolute, out var url);
            list.Add(new PackRating(
                game, language, pack, entry.Revision, entry.TriedAt, entry.PlayedAt, entry.LaterAt, Math.Max(0, entry.LaterCount), entry.Liked,
                verdict, verdict is null ? null : entry.ReportedAt, verdict is null ? null : url));
        }

        return list;
    }

    private void Save(List<PackRating> ratings)
    {
        if (_newerFormat)
        {
            return;
        }

        var json = new PackRatingsJson(CurrentSchemaVersion, [.. ratings.Select(r => new PackRatingJson(
            r.Game.ToString(),
            r.Language,
            r.PackId,
            r.Revision,
            r.TriedAt?.ToUniversalTime(),
            r.PlayedAt?.ToUniversalTime(),
            r.LaterAt?.ToUniversalTime(),
            r.LaterCount,
            r.Liked,
            r.Reported switch { PackVerdict.Works => "works", PackVerdict.DoesNotWork => "doesNotWork", _ => null },
            r.ReportedAt?.ToUniversalTime(),
            r.ReportUrl?.AbsoluteUri))]);
        try
        {
            AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(json, PackRatingJsonContext.Default.PackRatingsJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Saving {File} failed", _file);
        }
    }

    private void MoveAside(Exception? e)
    {
        var backup = $"{_file}.{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
        _logger.LogError(e, "{File} is damaged; moved to {Backup}", _file, backup);
        try
        {
            File.Move(_file, backup, overwrite: true);
        }
        catch (Exception move) when (move is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(move, "Can't move {File} aside", _file);
        }
    }
}

internal sealed record PackRatingsJson(int SchemaVersion, IReadOnlyList<PackRatingJson?>? Packs);

internal sealed record PackRatingJson(
    string? Game,
    string? Language,
    string? Pack,
    int Revision,
    DateTimeOffset? TriedAt,
    DateTimeOffset? PlayedAt,
    DateTimeOffset? LaterAt,
    int LaterCount,
    bool Liked,
    string? Reported,
    DateTimeOffset? ReportedAt,
    string? ReportUrl);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PackRatingsJson))]
internal sealed partial class PackRatingJsonContext : JsonSerializerContext;
