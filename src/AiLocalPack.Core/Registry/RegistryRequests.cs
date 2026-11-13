using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Requests;

namespace AiLocalPack.Core.Registry;

/// <summary>
/// An open translation request (task 74) of a game into a language, as the registry's Action collects it from the
/// request's Discussion: the players asking, when it was opened, and who is translating it now.
/// </summary>
/// <param name="Votes">Accounts that gave the Discussion a 👍 (bots left out), ≥ 1.</param>
/// <param name="Discussion">The request's Discussion (the oldest open one when there were several).</param>
/// <param name="Opened">When the request's Discussion was opened.</param>
/// <param name="Claim">The translator working on it, when a claim is live (<see cref="RequestRules.ClaimLifetime"/>).</param>
public sealed record RegistryRequest(int Votes, Uri Discussion, DateTimeOffset Opened, RegistryRequestClaim? Claim = null);

/// <summary>A live claim on a request: who, how far, and when their app last said so.</summary>
/// <param name="By">The translator's GitHub login.</param>
/// <param name="Stage">The generation stage while <see cref="RequestClaimState.Working"/>.</param>
/// <param name="Updated">The claim comment's last edit (the last news).</param>
public sealed record RegistryRequestClaim(string By, RequestClaimState State, GenerationStage? Stage, int Percent, DateTimeOffset Updated);

/// <summary>
/// <c>requests.json</c>, written by <c>requests collect</c> (task 74) and read by <c>index build --requests</c>:
/// <c>{schemaVersion: 1, requests: {"steam:620/ru": {votes, discussion, opened, claim?: {by, state, stage?, percent, updated}}}}</c>.
/// The same entry shape goes into <c>index.json</c> under <c>games.&lt;id&gt;.requests.&lt;language&gt;</c>.
/// </summary>
public static class RegistryRequests
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The requests, or every problem (the whole file is refused, like a ratings file).</summary>
    public static (IReadOnlyDictionary<RequestKey, RegistryRequest>? Requests, IReadOnlyList<PackFormatError> Errors) Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RequestsFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, RegistryRequestsJsonContext.Default.RequestsFileJson);
        }
        catch (JsonException e)
        {
            return (null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return (null, [new PackFormatError("", "The file holds no requests (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, CurrentSchemaVersion) is { } versionError)
        {
            return (null, [versionError]);
        }

        var errors = new PackFormatErrors();
        var requests = new Dictionary<RequestKey, RegistryRequest>();
        foreach (var (text, entry) in file.Requests ?? new Dictionary<string, RequestJson?>())
        {
            var field = PackFormat.Key("requests", text);
            if (!RequestKey.TryParse(text, out var key))
            {
                errors.Add(field, $"'{text}' is not a request (<game id>/<language>).");
                continue;
            }

            if (ReadEntry(entry, field, errors) is { } request)
            {
                requests[key] = request;
            }
        }

        return errors.Any ? (null, errors.List) : (requests, []);
    }

    /// <summary>The file <see cref="Read"/> reads: requests in ordinal order, indented, <c>\n</c> line ends and a trailing newline.</summary>
    public static string Write(IReadOnlyDictionary<RequestKey, RegistryRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var sorted = new SortedDictionary<string, RequestJson?>(StringComparer.Ordinal);
        foreach (var (key, value) in requests)
        {
            sorted[key.ToString()] = ToJson(value);
        }

        return JsonSerializer.Serialize(new RequestsFileJson(CurrentSchemaVersion, sorted), Writer.RequestsFileJson) + "\n";
    }

    private static readonly RegistryRequestsJsonContext Writer = new(new JsonSerializerOptions(RegistryRequestsJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>One entry (of this file or of the index); <c>null</c> with its errors added when it breaks a rule.</summary>
    internal static RegistryRequest? ReadEntry(RequestJson? entry, string field, PackFormatErrors errors)
    {
        if (entry is null)
        {
            errors.Add(field, "is empty.");
            return null;
        }

        var before = errors.List.Count;
        if (entry.Votes is not { } votes || votes < 1)
        {
            errors.Add($"{field}.votes", entry.Votes is null ? "is missing." : $"must be 1 or more, not {entry.Votes}.");
        }

        Uri? discussion = null;
        if (entry.Discussion is null)
        {
            errors.Add($"{field}.discussion", "is missing.");
        }
        else if (!Uri.TryCreate(entry.Discussion, UriKind.Absolute, out discussion) || discussion.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{field}.discussion", $"'{entry.Discussion}' is not an https:// URL.");
        }

        DateTimeOffset opened = default;
        if (entry.Opened is null)
        {
            errors.Add($"{field}.opened", "is missing.");
        }
        else if (!PackFormat.TryParseDate(entry.Opened, out opened))
        {
            errors.Add($"{field}.opened", $"'{entry.Opened}' is not a date and time with a time zone.");
        }

        RegistryRequestClaim? claim = null;
        if (entry.Claim is { } c)
        {
            var claimField = $"{field}.claim";
            if (!PackFormat.IsGitHubLogin(c.By ?? ""))
            {
                errors.Add($"{claimField}.by", $"'{c.By}' is not a GitHub login.");
            }

            if (!RequestClaimComment.TryState(c.State, out var state) || state == RequestClaimState.Stopped)
            {
                errors.Add($"{claimField}.state", $"'{c.State}' is not a claim state (working, built or published).");
            }

            GenerationStage? stage = null;
            if (c.Stage is not null)
            {
                if (RequestClaimComment.TryStage(c.Stage, out var s))
                {
                    stage = s;
                }
                else
                {
                    errors.Add($"{claimField}.stage", $"'{c.Stage}' is not a generation stage.");
                }
            }

            if (c.Percent is < 0 or > 100)
            {
                errors.Add($"{claimField}.percent", $"must be 0–100, not {c.Percent}.");
            }

            if (c.Updated is null || !PackFormat.TryParseDate(c.Updated, out var updated))
            {
                errors.Add($"{claimField}.updated", c.Updated is null ? "is missing." : $"'{c.Updated}' is not a date and time with a time zone.");
                updated = default;
            }

            claim = new RegistryRequestClaim(c.By ?? "", state, stage, c.Percent ?? 0, updated);
        }

        return errors.List.Count > before ? null : new RegistryRequest(entry.Votes!.Value, discussion!, opened, claim);
    }

    internal static RequestJson ToJson(RegistryRequest request) => new(
        request.Votes,
        request.Discussion.AbsoluteUri,
        PackFormat.FormatDate(request.Opened),
        request.Claim is { } c
            ? new RequestClaimJson(c.By, RequestClaimComment.StateText(c.State), c.Stage is { } s ? RequestClaimComment.StageText(s) : null, c.Percent, PackFormat.FormatDate(c.Updated))
            : null);
}

internal sealed record RequestsFileJson(int? SchemaVersion, IReadOnlyDictionary<string, RequestJson?>? Requests);

internal sealed record RequestJson(int? Votes, string? Discussion, string? Opened, RequestClaimJson? Claim);

internal sealed record RequestClaimJson(string? By, string? State, string? Stage, int? Percent, string? Updated);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(RequestsFileJson))]
internal sealed partial class RegistryRequestsJsonContext : JsonSerializerContext;
