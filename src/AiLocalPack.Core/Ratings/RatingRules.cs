using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Ratings;

/// <summary>When the app asks for a rating and what it lets the player send (task 63; design §3, §4). Pure.</summary>
public static class RatingRules
{
    /// <summary>"Later" twice for the same revision and the prompt stops asking; the Packs tab's rating block stays.</summary>
    public const int MaxLater = 2;

    /// <summary>
    /// The session counts as playing with the installed registry pack: it ended with the pack installed before it started
    /// (a session that began before the install doesn't count). Local packs are the player's own: never rated here.
    /// </summary>
    public static bool CountsAsPlayed(InstalledPack? record, GameSessionEnded ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        return record is { Local: false } && record.Game == ended.Game && ended.StartedAt >= record.InstalledAt;
    }

    /// <summary>
    /// The Packs tab's rating block (like, works / doesn't work) under the installed registry pack: shown once the player
    /// has played with the installed revision, not right after installing; not while a game update broke the pack.
    /// </summary>
    public static bool ShowBlock(InstalledPackState? installed, PackRating? rating) =>
        installed is { Local: false, Problem: InstalledPackProblem.None }
        && rating is { IsTried: true }
        && rating.PackId == installed.PackId
        && rating.Revision == installed.Revision;

    /// <summary>
    /// The game page's prompt ("How is «…»?" Works / Doesn't work / Later): after the player played with the installed
    /// revision, while it has a Discussion to post to, they aren't one of its authors and haven't reported this revision;
    /// "Later" hides it until the next session ends, and after <see cref="MaxLater"/> times for good.
    /// </summary>
    public static bool ShowPrompt(InstalledPackState? installed, PackRating? rating, bool hasDiscussion, bool isAuthor) =>
        ShowBlock(installed, rating)
        && hasDiscussion
        && !isAuthor
        && rating!.Reported is null
        && rating.LaterCount < MaxLater
        && (rating.LaterAt is not { } later || rating.PlayedAt > later);

    /// <summary>
    /// The signed-in player is credited on the pack (the index's author, or any author the manifest lists): they can't
    /// confirm their own pack, so Works / Doesn't work are hidden. Signed out (<paramref name="login"/> <c>null</c>) is
    /// nobody's author until the sign-in says otherwise.
    /// </summary>
    public static bool IsAuthor(string? login, PackAuthor indexAuthor, IEnumerable<PackAuthor>? manifestAuthors)
    {
        ArgumentNullException.ThrowIfNull(indexAuthor);
        if (string.IsNullOrEmpty(login))
        {
            return false;
        }

        return string.Equals(indexAuthor.GitHub, login, StringComparison.OrdinalIgnoreCase)
            || (manifestAuthors?.Any(a => string.Equals(a.GitHub, login, StringComparison.OrdinalIgnoreCase)) ?? false);
    }
}
