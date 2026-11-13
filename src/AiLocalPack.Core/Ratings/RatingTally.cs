using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Ratings;

/// <summary>A GitHub account as a Discussion shows it.</summary>
/// <param name="IsBot">A bot or app account (<c>__typename</c> <c>Bot</c>).</param>
/// <param name="CreatedAt">When the account was made; <c>null</c> when GitHub doesn't say (bots, organizations).</param>
public sealed record DiscussionUser(string Login, bool IsBot, DateTimeOffset? CreatedAt);

/// <summary>A top-level comment of a pack's Discussion.</summary>
/// <param name="Author"><c>null</c> for a deleted account (GitHub's "ghost").</param>
/// <param name="LastEditedAt">The latest edit (a player's later report of the same revision edits the earlier one).</param>
public sealed record DiscussionComment(string Id, DiscussionUser? Author, DateTimeOffset CreatedAt, DateTimeOffset? LastEditedAt, string Body)
{
    /// <summary>When it said what it says now.</summary>
    public DateTimeOffset SaidAt => LastEditedAt ?? CreatedAt;
}

/// <summary>A pack's Discussion as <c>ratings collect</c> reads it: who gave a 👍 and every top-level comment.</summary>
/// <param name="Pack">The pack its <see cref="PackDiscussionMarker"/> names; <c>null</c> for any other Discussion.</param>
public sealed record CollectedDiscussion(
    string Id,
    int Number,
    Uri Url,
    RegistryPackKey? Pack,
    IReadOnlyList<DiscussionUser> Likers,
    IReadOnlyList<DiscussionComment> Comments)
{
    /// <summary>The first post (a request's Discussion starts with its marker, task 74).</summary>
    public string Body { get; init; } = "";

    /// <summary>Closed on GitHub (a done or duplicate request).</summary>
    public bool Closed { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>What the rules need to know of a registry pack: its current revision, authors and Steam builds.</summary>
/// <param name="Authors">Every author the manifest lists (logins; their likes and reports don't count).</param>
/// <param name="BuildIds">The Steam build IDs it supports; <c>null</c> for an <c>fp:</c> game (its report's build can't be matched).</param>
public sealed record RatedPack(RegistryPackKey Key, string Title, int Revision, IReadOnlyList<string> Authors, IReadOnlySet<string>? BuildIds);

/// <summary>Why a report wasn't counted.</summary>
public enum IgnoredReport
{
    /// <summary>It names another pack (posted in the wrong Discussion).</summary>
    OtherPack,

    /// <summary>It is about an older (or unknown) revision.</summary>
    OtherRevision,

    /// <summary>The account's later report of the same revision replaces it.</summary>
    Replaced,

    /// <summary>The account was deleted.</summary>
    NoAccount,

    Bot,

    /// <summary>One of the pack's authors.</summary>
    Author,

    /// <summary>The account was younger than <see cref="RegistryVerification.MinAccountAge"/> when it reported.</summary>
    YoungAccount,

    /// <summary>A Steam pack's report from a build the pack doesn't list (or none).</summary>
    OtherBuild,
}

/// <summary>A pack's counted rating and what was left out of it, for the collector's log.</summary>
public sealed record PackTally(RegistryRating Rating, IReadOnlyDictionary<IgnoredReport, int> Ignored);

/// <summary>
/// The anti-abuse rules of ratings (task 64, decided; pure). Likes: 👍 givers except the pack's authors and bots.
/// Reports (<see cref="PackReportComment"/>, the marker only): about this pack and its current revision; one per
/// account, the latest said wins (its <see cref="DiscussionComment.SaidAt"/>, the later comment on a tie); then the
/// account must exist, not be a bot or an author (logins compared ignoring case), be at least
/// <see cref="RegistryVerification.MinAccountAge"/> old when the comment was posted, and a Steam pack's report must
/// name one of its builds. <see cref="RegistryVerification.IsConfirmed"/> turns the counts into <c>verified</c>.
/// </summary>
public static class RatingTally
{
    public static PackTally Count(RatedPack pack, CollectedDiscussion? discussion)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var ignored = new Dictionary<IgnoredReport, int>();
        void Ignore(IgnoredReport reason) => ignored[reason] = ignored.GetValueOrDefault(reason) + 1;
        if (discussion is null)
        {
            return new PackTally(RegistryRating.None, ignored);
        }

        var authors = pack.Authors.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var likes = discussion.Likers
            .Where(u => !u.IsBot && !authors.Contains(u.Login))
            .Select(u => u.Login)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var reports = new List<(DiscussionComment Comment, PackReport Report, int Order)>();
        for (var i = 0; i < discussion.Comments.Count; i++)
        {
            var comment = discussion.Comments[i];
            if (!PackReportComment.TryParse(comment.Body, out var report))
            {
                continue;
            }

            if (new RegistryPackKey(report.Game, report.Language, report.PackId) != pack.Key)
            {
                Ignore(IgnoredReport.OtherPack);
            }
            else if (report.Revision != pack.Revision)
            {
                Ignore(IgnoredReport.OtherRevision);
            }
            else if (comment.Author is null)
            {
                Ignore(IgnoredReport.NoAccount);
            }
            else
            {
                reports.Add((comment, report, i));
            }
        }

        var (works, doesNotWork) = (0, 0);
        foreach (var account in reports.GroupBy(r => r.Comment.Author!.Login, StringComparer.OrdinalIgnoreCase))
        {
            var latest = account.OrderBy(r => r.Comment.SaidAt).ThenBy(r => r.Order).Last();
            for (var i = 1; i < account.Count(); i++)
            {
                Ignore(IgnoredReport.Replaced);
            }

            var author = latest.Comment.Author!;
            if (author.IsBot)
            {
                Ignore(IgnoredReport.Bot);
            }
            else if (authors.Contains(author.Login))
            {
                Ignore(IgnoredReport.Author);
            }
            else if (author.CreatedAt is not { } created || latest.Comment.CreatedAt - created < RegistryVerification.MinAccountAge)
            {
                Ignore(IgnoredReport.YoungAccount);
            }
            else if (pack.BuildIds is { } builds && (latest.Report.Build is not { } build || !builds.Contains(build)))
            {
                Ignore(IgnoredReport.OtherBuild);
            }
            else if (latest.Report.Verdict == PackVerdict.Works)
            {
                works++;
            }
            else
            {
                doesNotWork++;
            }
        }

        return new PackTally(new RegistryRating(likes, works, doesNotWork), ignored);
    }
}

/// <summary>
/// The first line of a pack's Discussion, which ties it to the pack (task 64, decided):
/// <c>&lt;!-- ailocalpack-pack steam:620/ru/full --&gt;</c>, an HTML comment GitHub doesn't render. Only maintainers
/// and the registry's Action can open Discussions in the <c>Packs</c> category (Announcement format), so players can't
/// make another one for a pack.
/// </summary>
public static partial class PackDiscussionMarker
{
    public static string Format(RegistryPackKey pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return $"<!-- ailocalpack-pack {pack} -->";
    }

    /// <summary>The pack a Discussion body names at its very start, or <c>false</c>.</summary>
    public static bool TryParse(string? body, [NotNullWhen(true)] out RegistryPackKey? pack)
    {
        pack = null;
        if (body is null || MarkerPattern().Match(body) is not { Success: true } match || !RegistryPackKey.TryParse(match.Groups["pack"].Value, out var key))
        {
            return false;
        }

        pack = key;
        return true;
    }

    [GeneratedRegex(@"\A\s*<!--\s*ailocalpack-pack\s+(?<pack>[^\s]+)\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}
