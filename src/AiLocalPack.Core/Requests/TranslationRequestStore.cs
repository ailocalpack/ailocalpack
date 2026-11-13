using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Requests;

/// <summary>A request this player made (task 74), kept to tell them when it is taken and when it is done.</summary>
/// <param name="Discussion">The request's Discussion when the player asked.</param>
/// <param name="TakenBy">The translator the player was told about (once per translator).</param>
/// <param name="DoneAt">When the app saw a pack for it (told once).</param>
/// <param name="DoneSeen">The player closed the Text tab's "done" strip (or installed a pack).</param>
public sealed record AskedRequest(
    RequestKey Key,
    Uri? Discussion,
    DateTimeOffset AskedAt,
    string? TakenBy = null,
    DateTimeOffset? DoneAt = null,
    bool DoneSeen = false);

/// <summary>The claim one of this player's generation jobs keeps on a request.</summary>
/// <param name="Job">The job's ID.</param>
/// <param name="Comment">The claim comment's node ID once it is posted.</param>
/// <param name="Sent">What the comment says now (<c>null</c> until the first one went through).</param>
public sealed record RequestClaimRecord(string Job, RequestKey Key, Uri Discussion, string? Comment = null, RequestClaim? Sent = null);

/// <summary>
/// <c>&lt;data&gt;/translation-requests.json</c> (task 74): the player's own requests and the claims of their jobs. Kept in
/// memory after the first read; atomic writes; a damaged file is moved aside as <c>.&lt;utc&gt;.bak</c> and read as empty;
/// a newer-format file is read as empty and never overwritten. Thread-safe; never throws for disk state.
/// </summary>
public sealed class TranslationRequestStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private (List<AskedRequest> Asked, List<RequestClaimRecord> Claims)? _data;
    private bool _newerFormat;

    public TranslationRequestStore(AppPaths paths, TimeProvider? time = null, ILogger<TranslationRequestStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = paths.TranslationRequestsFile;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>A record of the game changed (any thread).</summary>
    public event EventHandler<GameId>? Changed;

    public AskedRequest? Find(RequestKey key)
    {
        lock (_lock)
        {
            return Data().Asked.FirstOrDefault(r => r.Key == key);
        }
    }

    public IReadOnlyList<AskedRequest> Asked()
    {
        lock (_lock)
        {
            return [.. Data().Asked];
        }
    }

    /// <summary>The player asked (again): a new record, or the old one with the newer Discussion; "done" starts over.</summary>
    public AskedRequest Ask(RequestKey key, Uri? discussion, DateTimeOffset at) =>
        ChangeAsked(key, r => r is null ? new AskedRequest(key, discussion, at) : r with { Discussion = discussion ?? r.Discussion, DoneAt = null, DoneSeen = false })!;

    /// <summary>The player withdrew: the record goes.</summary>
    public void Withdraw(RequestKey key) => ChangeAsked(key, _ => null);

    public AskedRequest? Taken(RequestKey key, string by) => ChangeAsked(key, r => r is null ? null : r with { TakenBy = by });

    public AskedRequest? Done(RequestKey key, DateTimeOffset at) => ChangeAsked(key, r => r is null ? null : r with { DoneAt = r.DoneAt ?? at });

    public AskedRequest? SeenDone(RequestKey key) => ChangeAsked(key, r => r is null ? null : r with { DoneSeen = true });

    public RequestClaimRecord? FindClaim(string job)
    {
        lock (_lock)
        {
            return Data().Claims.FirstOrDefault(c => c.Job == job);
        }
    }

    public IReadOnlyList<RequestClaimRecord> Claims()
    {
        lock (_lock)
        {
            return [.. Data().Claims];
        }
    }

    /// <summary>Adds or replaces the claim of <see cref="RequestClaimRecord.Job"/>.</summary>
    public void SetClaim(RequestClaimRecord claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_lock)
        {
            var claims = Data().Claims;
            var index = claims.FindIndex(c => c.Job == claim.Job);
            if (index >= 0 && claims[index] == claim)
            {
                return;
            }

            if (index >= 0)
            {
                claims[index] = claim;
            }
            else
            {
                claims.Add(claim);
            }

            Save();
        }

        Changed?.Invoke(this, claim.Key.Game);
    }

    public void RemoveClaim(string job)
    {
        RequestClaimRecord? removed;
        lock (_lock)
        {
            removed = Data().Claims.FirstOrDefault(c => c.Job == job);
            if (removed is null)
            {
                return;
            }

            Data().Claims.Remove(removed);
            Save();
        }

        Changed?.Invoke(this, removed.Key.Game);
    }

    private AskedRequest? ChangeAsked(RequestKey key, Func<AskedRequest?, AskedRequest?> change)
    {
        ArgumentNullException.ThrowIfNull(key);
        AskedRequest? updated;
        lock (_lock)
        {
            var asked = Data().Asked;
            var index = asked.FindIndex(r => r.Key == key);
            var current = index >= 0 ? asked[index] : null;
            updated = change(current);
            if (updated == current)
            {
                return updated;
            }

            if (updated is null)
            {
                asked.RemoveAt(index);
            }
            else if (index >= 0)
            {
                asked[index] = updated;
            }
            else
            {
                asked.Add(updated);
            }

            Save();
        }

        Changed?.Invoke(this, key.Game);
        return updated;
    }

    private (List<AskedRequest> Asked, List<RequestClaimRecord> Claims) Data() => _data ??= Load();

    private (List<AskedRequest>, List<RequestClaimRecord>) Load()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(_file))
            {
                return ([], []);
            }

            bytes = File.ReadAllBytes(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't read {File}", _file);
            return ([], []);
        }

        TranslationRequestsJson? json;
        try
        {
            json = JsonSerializer.Deserialize(bytes, TranslationRequestsJsonContext.Default.TranslationRequestsJson);
        }
        catch (JsonException e)
        {
            MoveAside(e);
            return ([], []);
        }

        if (json is null)
        {
            MoveAside(null);
            return ([], []);
        }

        if (json.SchemaVersion > CurrentSchemaVersion)
        {
            _logger.LogWarning("{File} has schema {Version}, newer than this app; read as empty and left as it is", _file, json.SchemaVersion);
            _newerFormat = true;
            return ([], []);
        }

        var asked = new List<AskedRequest>();
        foreach (var entry in json.Asked ?? [])
        {
            if (entry is null || !RequestKey.TryParse($"{entry.Game}/{entry.Language}", out var key) || asked.Any(a => a.Key == key))
            {
                _logger.LogWarning("Skipping a damaged request of {File}", _file);
                continue;
            }

            asked.Add(new AskedRequest(key, Link(entry.Discussion), entry.AskedAt, entry.TakenBy, entry.DoneAt, entry.DoneSeen));
        }

        var claims = new List<RequestClaimRecord>();
        foreach (var entry in json.Claims ?? [])
        {
            if (entry is null || entry.Job is not { Length: > 0 } job || !RequestKey.TryParse($"{entry.Game}/{entry.Language}", out var key)
                || Link(entry.Discussion) is not { } discussion || claims.Any(c => c.Job == job))
            {
                _logger.LogWarning("Skipping a damaged claim of {File}", _file);
                continue;
            }

            RequestClaim? sent = null;
            if (RequestClaimComment.TryState(entry.State, out var state))
            {
                sent = new RequestClaim(
                    key,
                    state,
                    RequestClaimComment.TryStage(entry.Stage, out var stage) ? stage : null,
                    Math.Clamp(entry.Percent, 0, 100),
                    entry.Agent,
                    Enum.TryParse<GenerationQuality>(entry.Quality, ignoreCase: true, out var quality) && Enum.IsDefined(quality) ? quality : null);
            }

            claims.Add(new RequestClaimRecord(job, key, discussion, entry.Comment is { Length: > 0 } c ? c : null, sent));
        }

        return (asked, claims);
    }

    private void Save()
    {
        if (_newerFormat || _data is not { } data)
        {
            return;
        }

        var json = new TranslationRequestsJson(
            CurrentSchemaVersion,
            [.. data.Asked.Select(a => new AskedRequestJson(
                a.Key.Game.ToString(), a.Key.Language, a.Discussion?.AbsoluteUri, a.AskedAt.ToUniversalTime(), a.TakenBy, a.DoneAt?.ToUniversalTime(), a.DoneSeen))],
            [.. data.Claims.Select(c => new RequestClaimJson(
                c.Job,
                c.Key.Game.ToString(),
                c.Key.Language,
                c.Discussion.AbsoluteUri,
                c.Comment,
                c.Sent is { } s ? RequestClaimComment.StateText(s.State) : null,
                c.Sent?.Stage is { } stage ? RequestClaimComment.StageText(stage) : null,
                c.Sent?.Percent ?? 0,
                c.Sent?.Agent,
                c.Sent?.Quality?.ToString().ToLowerInvariant()))]);
        try
        {
            AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(json, TranslationRequestsJsonContext.Default.TranslationRequestsJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Saving {File} failed", _file);
        }
    }

    private static Uri? Link(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;

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

internal sealed record TranslationRequestsJson(int SchemaVersion, IReadOnlyList<AskedRequestJson?>? Asked, IReadOnlyList<RequestClaimJson?>? Claims);

internal sealed record AskedRequestJson(
    string? Game,
    string? Language,
    string? Discussion,
    DateTimeOffset AskedAt,
    string? TakenBy,
    DateTimeOffset? DoneAt,
    bool DoneSeen);

internal sealed record RequestClaimJson(
    string? Job,
    string? Game,
    string? Language,
    string? Discussion,
    string? Comment,
    string? State,
    string? Stage,
    int Percent,
    string? Agent,
    string? Quality);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TranslationRequestsJson))]
internal sealed partial class TranslationRequestsJsonContext : JsonSerializerContext;
