using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Requests;

/// <summary>What became of a request in a collector run.</summary>
public enum RequestOutcome
{
    /// <summary>Open: published in the index.</summary>
    Open,

    /// <summary>The registry has a pack for the game + language now: done (closed with <c>--close</c>).</summary>
    Done,

    /// <summary>The game's publisher opted out: left out.</summary>
    OptedOut,

    /// <summary>Every 👍 was taken back: left out until someone asks again.</summary>
    NoVotes,
}

/// <summary>One request's line in the collector's log.</summary>
/// <param name="Discussions">The open Discussions naming it (the first is the one the index links).</param>
/// <param name="Closed">How many of them this run closed (done, or duplicates).</param>
public sealed record CollectedRequest(RequestKey Key, RequestOutcome Outcome, int Votes, IReadOnlyList<Uri> Discussions, RegistryRequestClaim? Claim, int Closed);

/// <summary>What <see cref="RequestCollector.CollectAsync"/> gathered: the requests file's content, per request, and warnings.</summary>
public sealed record RequestCollection(
    IReadOnlyDictionary<RequestKey, RegistryRequest> Requests,
    IReadOnlyList<CollectedRequest> Lines,
    IReadOnlyList<RegistryBuildProblem> Warnings);

/// <summary>
/// <c>requests collect</c> (task 74): reads every open Discussion of the registry's <see cref="RequestQueries.Category"/>
/// category, ties each to its request by <see cref="RequestDiscussionMarker"/>, and counts per request: votes = the
/// accounts (not bots) that gave any of its Discussions a 👍, the oldest Discussion as its link and opening date, the
/// newest live claim comment (<see cref="RequestRules.IsLive"/>; not a bot's or a deleted account's). A request whose game +
/// language has a pack in the checkout is done; with <c>close</c> its Discussions get a comment naming the pack and are
/// closed as resolved, and newer duplicates of an open request are closed pointing at the oldest. A missing category is a
/// warning (no requests), so the index still publishes. Fails as a whole on any GitHub error.
/// </summary>
public sealed class RequestCollector(RegistryDiscussionClient client, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<RatingResult<RequestCollection>> CollectAsync(string root, RegistryRepo repo, bool close, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(repo);
        var warnings = new List<RegistryBuildProblem>();
        var packs = RatingCollector.ReadPacks(root, warnings);
        var optOuts = ReadOptOuts(root, warnings);

        var category = await client.CategoryAsync(repo, RequestQueries.Category, cancellationToken).ConfigureAwait(false);
        if (!category.IsOk)
        {
            return RatingResult<RequestCollection>.Failed(category.Error);
        }

        if (category.Value.CategoryId is not { } categoryId)
        {
            warnings.Add(new RegistryBuildProblem("", "", $"{repo.FullName} has no Discussion category \"{RequestQueries.Category}\" (made by hand in the repository settings); no requests collected."));
            return RatingResult<RequestCollection>.Ok(new RequestCollection(new Dictionary<RequestKey, RegistryRequest>(), [], warnings));
        }

        var listed = await client.ListAsync(repo, categoryId, cancellationToken).ConfigureAwait(false);
        if (!listed.IsOk)
        {
            return RatingResult<RequestCollection>.Failed(listed.Error);
        }

        var now = _time.GetUtcNow();
        var requests = new Dictionary<RequestKey, RegistryRequest>();
        var lines = new List<CollectedRequest>();
        var groups = listed.Value
            .Where(d => !d.Closed)
            .Select(d => (Discussion: d, Key: RequestDiscussionMarker.TryParse(d.Body, out var key) ? key : null))
            .Where(d => d.Key is not null)
            .GroupBy(d => d.Key!, d => d.Discussion)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var key = group.Key;
            var discussions = group.OrderBy(d => d.Number).ToList();
            var oldest = discussions[0];
            var votes = discussions.SelectMany(d => d.Likers).Where(u => !u.IsBot).Select(u => u.Login).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var claim = discussions
                .SelectMany(d => d.Comments)
                .Select(c => RequestClaimComment.TryParse(c.Body, out var parsed) && parsed.Key == key && c.Author is { IsBot: false } ? (Comment: c, Claim: parsed) : default)
                .Where(c => c.Claim is not null && RequestRules.IsLive(c.Claim.State, c.Comment.SaidAt, now))
                .OrderBy(c => c.Comment.SaidAt)
                .Select(c => new RegistryRequestClaim(c.Comment.Author!.Login, c.Claim!.State, c.Claim.State == RequestClaimState.Working ? c.Claim.Stage : null, c.Claim.Percent, c.Comment.SaidAt))
                .LastOrDefault();

            var done = packs.Where(p => p.Key.Game == key.Game && p.Key.Language == key.Language).OrderBy(p => p.Key.Id, StringComparer.Ordinal).ToList();
            var outcome = done.Count > 0 ? RequestOutcome.Done
                : optOuts.Contains(key.Game) ? RequestOutcome.OptedOut
                : votes == 0 ? RequestOutcome.NoVotes
                : RequestOutcome.Open;
            var closed = 0;
            if (close && outcome == RequestOutcome.Done)
            {
                var names = string.Join(", ", done.Select(p => $"«{p.Title}»"));
                var body = $"""
                    ✅ **Done**: the registry has a translation now: {names}. Install it in {AppInfo.DisplayName} (game page → *Text*), and after playing, tell others whether it works.

                    <sub>Closed by the registry's Action.</sub>
                    """;
                foreach (var discussion in discussions)
                {
                    var finished = await CloseWithAsync(discussion, body, "RESOLVED", cancellationToken).ConfigureAwait(false);
                    if (!finished.IsOk)
                    {
                        return RatingResult<RequestCollection>.Failed(finished.Error with { Message = $"Can't close {discussion.Url}: {finished.Error.Message}" });
                    }

                    closed++;
                }
            }
            else if (close && outcome == RequestOutcome.Open)
            {
                foreach (var duplicate in discussions.Skip(1))
                {
                    var body = $"""
                        This asks for the same translation as {oldest.Url}; the votes of both count there. Closed as a duplicate.

                        <sub>Closed by the registry's Action.</sub>
                        """;
                    var finished = await CloseWithAsync(duplicate, body, "DUPLICATE", cancellationToken).ConfigureAwait(false);
                    if (!finished.IsOk)
                    {
                        return RatingResult<RequestCollection>.Failed(finished.Error with { Message = $"Can't close {duplicate.Url}: {finished.Error.Message}" });
                    }

                    closed++;
                }
            }
            else if (outcome == RequestOutcome.Open && discussions.Count > 1)
            {
                warnings.Add(new RegistryBuildProblem("", "", $"{string.Join(", ", discussions.Skip(1).Select(d => d.Url))} ask for {key} too; their votes count with {oldest.Url}."));
            }

            if (!GameLanguages.Tags.Contains(key.Language, StringComparer.Ordinal))
            {
                continue;
            }

            if (outcome == RequestOutcome.Open)
            {
                requests[key] = new RegistryRequest(votes, oldest.Url, oldest.CreatedAt ?? now, claim);
            }

            lines.Add(new CollectedRequest(key, outcome, votes, [.. discussions.Select(d => d.Url)], claim, closed));
        }

        return RatingResult<RequestCollection>.Ok(new RequestCollection(requests, lines, warnings));
    }

    private async Task<RatingResult<bool>> CloseWithAsync(CollectedDiscussion discussion, string comment, string reason, CancellationToken cancellationToken)
    {
        var commented = await client.CommentAsync(discussion.Id, comment, cancellationToken).ConfigureAwait(false);
        return commented.IsOk ? await client.CloseAsync(discussion.Id, reason, cancellationToken).ConfigureAwait(false) : commented;
    }

    /// <summary>The opted-out games of the checkout's policy; an unreadable file is a warning (none), since index build refuses it anyway.</summary>
    private static HashSet<GameId> ReadOptOuts(string root, List<RegistryBuildProblem> warnings)
    {
        var file = Path.Combine(root, RegistryOptOuts.Path);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            var (games, errors) = RegistryOptOuts.Read(File.ReadAllText(file));
            warnings.AddRange(errors.Select(e => new RegistryBuildProblem(RegistryOptOuts.Path, e.Field, e.Message)));
            return games is null ? [] : [.. games];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new RegistryBuildProblem(RegistryOptOuts.Path, "", $"can't be read: {e.Message}"));
            return [];
        }
    }
}
