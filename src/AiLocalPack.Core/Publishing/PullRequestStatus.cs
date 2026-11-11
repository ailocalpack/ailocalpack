using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AiLocalPack.Core.GitHub;

namespace AiLocalPack.Core.Publishing;

/// <summary>Where a pack's pull request stands on GitHub.</summary>
public enum PullRequestState
{
    Open,

    /// <summary>Merged: the pack is in the registry as <c>alpha</c> (the index lists it after <i>Publish index</i>).</summary>
    Merged,

    /// <summary>Closed without merging (by a maintainer or the player).</summary>
    Closed,
}

/// <summary>What registry CI says about the PR's head commit.</summary>
public enum CiState
{
    /// <summary>Not started (no check runs yet) or running.</summary>
    Pending,

    /// <summary>Every check passed: auto-merge follows.</summary>
    Passed,

    /// <summary>A check failed (failure, timed out, action required).</summary>
    Failed,
}

public enum CiAnnotationLevel
{
    Notice,
    Warning,
    Failure,
}

/// <summary>One check annotation (a <c>::error file=…,title=…::message</c> line of a workflow).</summary>
/// <param name="Path">The file it is about, relative to the repository (<c>games/…/manifest.json</c>), or <c>null</c>.</param>
/// <param name="Title">The workflow's title (the pack validator puts the manifest field there), or <c>null</c>.</param>
public sealed record CiAnnotation(string? Path, CiAnnotationLevel Level, string? Title, string Message);

/// <summary>A pull request's state and its CI result, as last read.</summary>
/// <param name="HeadSha">The PR's head commit (a push to the branch changes it and starts CI again).</param>
/// <param name="Annotations">The failed checks' annotations when <see cref="Ci"/> is <see cref="CiState.Failed"/>.</param>
/// <param name="ClosedAt">When it was merged or closed.</param>
public sealed record PullRequestStatus(
    int Number,
    PullRequestState State,
    string HeadSha,
    CiState Ci,
    IReadOnlyList<CiAnnotation> Annotations,
    DateTimeOffset? ClosedAt = null);

/// <summary>How <see cref="PullRequestStatusClient.CheckAsync"/> ended.</summary>
public abstract record PullRequestCheckResult
{
    private PullRequestCheckResult()
    {
    }

    public sealed record Checked(PullRequestStatus Status) : PullRequestCheckResult;

    /// <summary>GitHub couldn't be asked (offline, rate limit with <see cref="GitHubApiError.RetryAt"/>, 404…).</summary>
    public sealed record Failed(GitHubApiError Error) : PullRequestCheckResult;
}

/// <summary>
/// Reads a pack PR's state and CI result (task 62): <c>GET /repos/&lt;registry&gt;/pulls/&lt;n&gt;</c> (state, merged,
/// head SHA), then for an open PR <c>GET /commits/&lt;head&gt;/check-runs</c> (<see cref="PullRequestStatuses.Ci"/>),
/// and for a failed run its annotations (<c>/check-runs/&lt;id&gt;/annotations</c>, kept per run ID: a completed run's
/// annotations never change). Both reads are conditional (<see cref="GitHubEtagCache"/>, a 304 costs no rate limit).
/// The token is the player's when signed in (5000 requests an hour), else none (public data, 60 an hour). Never
/// throws except for cancellation.
/// </summary>
public sealed class PullRequestStatusClient(HttpClient http, GitHubOAuthApp app, TimeProvider? time = null)
{
    private readonly GitHubEtagCache _cache = new();
    private readonly ConcurrentDictionary<long, IReadOnlyList<CiAnnotation>> _annotations = new();

    public async Task<PullRequestCheckResult> CheckAsync(RegistryRepo registry, int number, string? token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var api = new GitHubRestClient(http, app, token, time);
        try
        {
            var pull = await api.GetAsync($"repos/{registry.FullName}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", _cache, cancellationToken).ConfigureAwait(false);
            var (state, head, closedAt) = PullRequestStatuses.Pull(pull);
            if (head is null)
            {
                return new PullRequestCheckResult.Failed(new GitHubApiError(PublishProblem.Unexpected, 200, $"GitHub's answer for PR #{number} has no head commit."));
            }

            if (state != PullRequestState.Open)
            {
                return new PullRequestCheckResult.Checked(new PullRequestStatus(number, state, head, CiState.Passed, [], closedAt));
            }

            var runs = await api.GetAsync($"repos/{registry.FullName}/commits/{head}/check-runs?per_page=100", _cache, cancellationToken).ConfigureAwait(false);
            var (ci, failed) = PullRequestStatuses.Ci(runs);
            var annotations = new List<CiAnnotation>();
            foreach (var (id, count) in failed)
            {
                if (count > 0)
                {
                    annotations.AddRange(await AnnotationsAsync(api, registry, id, cancellationToken).ConfigureAwait(false));
                }
            }

            return new PullRequestCheckResult.Checked(new PullRequestStatus(number, state, head, ci, annotations));
        }
        catch (GitHubApiException e)
        {
            return new PullRequestCheckResult.Failed(e.Error);
        }
    }

    private async Task<IReadOnlyList<CiAnnotation>> AnnotationsAsync(GitHubRestClient api, RegistryRepo registry, long run, CancellationToken cancellationToken)
    {
        if (_annotations.TryGetValue(run, out var known))
        {
            return known;
        }

        var list = new List<CiAnnotation>();
        for (var page = 1; page <= 4; page++)
        {
            var json = await api.GetAsync($"repos/{registry.FullName}/check-runs/{run.ToString(CultureInfo.InvariantCulture)}/annotations?per_page=50&page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            var read = CiAnnotations.Parse(json);
            list.AddRange(read);
            if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() < 50)
            {
                break;
            }
        }

        _annotations[run] = list;
        return list;
    }
}

/// <summary>GitHub's pull request and check run answers read into states (pure).</summary>
public static class PullRequestStatuses
{
    /// <summary>A pull request's state (merged wins over closed), head SHA and when it was merged or closed.</summary>
    public static (PullRequestState State, string? Head, DateTimeOffset? ClosedAt) Pull(JsonElement pull)
    {
        var head = Text(Property(pull, "head"), "sha");
        if (Property(pull, "merged") is { ValueKind: JsonValueKind.True } || Date(pull, "merged_at") is not null)
        {
            return (PullRequestState.Merged, head, Date(pull, "merged_at"));
        }

        return Text(pull, "state") == "closed"
            ? (PullRequestState.Closed, head, Date(pull, "closed_at"))
            : (PullRequestState.Open, head, null);
    }

    /// <summary>
    /// The head commit's CI: a run not completed → <see cref="CiState.Pending"/>; else a run that ended in
    /// <c>failure</c>, <c>timed_out</c>, <c>action_required</c> or <c>startup_failure</c> → <see cref="CiState.Failed"/>
    /// (with each such run's ID and annotation count); else, with at least one run that succeeded,
    /// <see cref="CiState.Passed"/>. Cancelled, skipped and neutral runs count for nothing (a newer run replaces a
    /// cancelled one); no runs at all is <see cref="CiState.Pending"/> (CI not started yet).
    /// </summary>
    public static (CiState State, IReadOnlyList<(long Run, int Annotations)> Failed) Ci(JsonElement checkRuns)
    {
        var runs = Property(checkRuns, "check_runs") is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToList() : [];
        if (runs.Any(r => Text(r, "status") is not "completed"))
        {
            return (CiState.Pending, []);
        }

        var failed = runs
            .Where(r => Text(r, "conclusion") is "failure" or "timed_out" or "action_required" or "startup_failure")
            .Select(r => (Long(r, "id") ?? 0, (int)(Long(Property(r, "output"), "annotations_count") ?? 0)))
            .ToList();
        if (failed.Count > 0)
        {
            return (CiState.Failed, failed);
        }

        return runs.Any(r => Text(r, "conclusion") == "success") ? (CiState.Passed, []) : (CiState.Pending, []);
    }

    internal static JsonElement Property(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;

    internal static string? Text(JsonElement json, string name) =>
        Property(json, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? Long(JsonElement json, string name) =>
        Property(json, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static DateTimeOffset? Date(JsonElement json, string name) =>
        DateTimeOffset.TryParse(Text(json, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

/// <summary>Check annotations read from GitHub (pure).</summary>
public static class CiAnnotations
{
    /// <summary>
    /// The annotations of a <c>/check-runs/&lt;id&gt;/annotations</c> answer; an entry without a message is skipped, a
    /// path of <c>.github</c> (GitHub's own "Process completed with exit code 1" line) is none.
    /// </summary>
    public static IReadOnlyList<CiAnnotation> Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CiAnnotation>();
        foreach (var entry in json.EnumerateArray())
        {
            if (PullRequestStatuses.Text(entry, "message") is not { Length: > 0 } message)
            {
                continue;
            }

            var level = PullRequestStatuses.Text(entry, "annotation_level") switch
            {
                "failure" => CiAnnotationLevel.Failure,
                "warning" => CiAnnotationLevel.Warning,
                _ => CiAnnotationLevel.Notice,
            };
            var path = PullRequestStatuses.Text(entry, "path") is { Length: > 0 } p && p != ".github" ? p : null;
            var title = PullRequestStatuses.Text(entry, "title") is { Length: > 0 } t ? t : null;
            list.Add(new CiAnnotation(path, level, title, message.Trim()));
        }

        return list;
    }
}

/// <summary>Why registry CI rejected a pack PR, as far as its annotations tell.</summary>
public enum CiFailureKind
{
    /// <summary>"One pack per pull request": the branch changes more than the pack's folder.</summary>
    Scope,

    /// <summary>The manifest's status isn't <c>alpha</c>.</summary>
    Status,

    /// <summary>The patch archive in the player's fork is missing or doesn't match the manifest.</summary>
    Patch,

    /// <summary>The pack validator refused a file of the pack (a field of the manifest, the string table…).</summary>
    Pack,

    /// <summary>A check about the registry itself (index build, schemas, the CLI): not the pack's fault as far as the app can tell.</summary>
    Registry,

    /// <summary>CI failed without saying why (no annotation).</summary>
    Unknown,
}

/// <summary>A rejected PR's reason: the kind and the failure annotations behind it (generic exit code lines left out).</summary>
public sealed record CiFailure(CiFailureKind Kind, IReadOnlyList<CiAnnotation> Problems)
{
    /// <summary>The first problem: "field: message" (the validator's field is the title).</summary>
    public string? FirstProblem => Problems.Count == 0 ? null : CiFailures.Describe(Problems[0]);
}

/// <summary>Classifies a rejected PR's annotations (pure; the registry's workflows and scripts write them).</summary>
public static class CiFailures
{
    /// <summary>The title of <c>validate.yml</c>'s scope error.</summary>
    public const string ScopeTitle = "One pack per pull request";

    /// <summary>
    /// The failure annotations that say something (not "Process completed with exit code N"), classified by the first
    /// one: the scope title → <see cref="CiFailureKind.Scope"/>; on the pack's folder (<paramref name="packFolder"/>,
    /// <c>games/&lt;game&gt;/&lt;language&gt;/&lt;id&gt;</c>) titled <c>status</c> → <see cref="CiFailureKind.Status"/>,
    /// <c>patch</c> → <see cref="CiFailureKind.Patch"/>, else <see cref="CiFailureKind.Pack"/>; anything else →
    /// <see cref="CiFailureKind.Registry"/>; none → <see cref="CiFailureKind.Unknown"/>.
    /// </summary>
    public static CiFailure Explain(IReadOnlyList<CiAnnotation> annotations, string packFolder)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(packFolder);
        var problems = annotations
            .Where(a => a.Level == CiAnnotationLevel.Failure && !a.Message.StartsWith("Process completed with exit code", StringComparison.Ordinal))
            .ToList();
        if (problems.Count == 0)
        {
            return new CiFailure(CiFailureKind.Unknown, []);
        }

        var first = problems[0];
        var prefix = packFolder.TrimEnd('/') + "/";
        var kind = first switch
        {
            { Title: ScopeTitle } => CiFailureKind.Scope,
            { Path: { } path } when path.StartsWith(prefix, StringComparison.Ordinal) => first.Title switch
            {
                "status" => CiFailureKind.Status,
                "patch" => CiFailureKind.Patch,
                _ => CiFailureKind.Pack,
            },
            _ => CiFailureKind.Registry,
        };
        return new CiFailure(kind, problems);
    }

    /// <summary>"field: message", the message alone without a title.</summary>
    public static string Describe(CiAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var message = annotation.Message.TrimEnd('.');
        return annotation.Title is { } title && title != ScopeTitle && !message.StartsWith(title, StringComparison.Ordinal) ? $"{title}: {message}" : message;
    }
}

/// <summary>When to ask GitHub about a pack PR again (pure; task 62).</summary>
/// <param name="State">The PR's state as last read; <c>null</c> before the first read.</param>
/// <param name="Ci">Its CI as last read.</param>
/// <param name="Since">When the state last changed (or the pack was sent).</param>
/// <param name="Errors">Failed reads in a row.</param>
/// <param name="RetryAt">GitHub's rate limit reset, when the last read hit it.</param>
public sealed record PullRequestPoll(PullRequestState? State, CiState? Ci, DateTimeOffset Since, int Errors = 0, DateTimeOffset? RetryAt = null);

public static class PullRequestPolling
{
    /// <summary>The longest wait between two reads.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The next read: none once merged or closed. After failed reads in a row: 1, 2, 4… minutes, at most
    /// <see cref="MaxWait"/>, and never before a rate limit's reset. A rejected PR: every 10 minutes (the player may
    /// fix it on GitHub). CI running or passed (merging): every minute for the first 15 minutes after a change, then
    /// every 5 minutes for 2 hours, then every 30.
    /// </summary>
    public static DateTimeOffset? Next(PullRequestPoll poll, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(poll);
        if (poll.Errors > 0)
        {
            var backoff = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(poll.Errors - 1, 10)), MaxWait.TotalMinutes));
            var next = now + backoff;
            return poll.RetryAt is { } reset && reset > next ? reset : next;
        }

        if (poll.State is PullRequestState.Merged or PullRequestState.Closed)
        {
            return null;
        }

        if (poll.Ci == CiState.Failed)
        {
            return now + TimeSpan.FromMinutes(10);
        }

        var age = now - poll.Since;
        return now + (age < TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(1) : age < TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(5) : MaxWait);
    }
}
