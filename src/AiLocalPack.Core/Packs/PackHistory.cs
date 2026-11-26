namespace AiLocalPack.Core.Packs;

/// <summary>
/// Registry CI's check of a pack PR's history against the registry as merged (decided 2026-11-26, <c>pack validate
/// --base</c>): a pack PR may only add to a pack, never rewrite who made it. The publish client always writes such a
/// manifest; the check stops a hand-made PR that would take an author off, rewrite or cut the revisions, or start a
/// "new" pack with a made-up history.
/// </summary>
public static class PackHistory
{
    /// <summary>The field registry CI's annotation is titled with.</summary>
    public const string ProblemField = "history";

    /// <summary>
    /// The problems of <paramref name="revision"/> against <paramref name="merged"/>, the merged manifest of the same
    /// pack folder (<c>null</c> = a new pack): a new pack is revision 1; a revision of a merged pack is its revision + 1,
    /// keeps its <c>created</c> date and every earlier revision as it was, and lists the merged authors first, in their
    /// order (logins compared case-insensitively; a display name may change). Empty = nothing to say.
    /// </summary>
    public static IReadOnlyList<PackProblem> Problems(PackManifest? merged, PackManifest revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var problems = new List<PackProblem>();
        if (merged is null)
        {
            if (revision.Revision != 1)
            {
                problems.Add(Problem($"a new pack starts at revision 1, not {revision.Revision}."));
            }

            return problems;
        }

        if (revision.Revision != merged.Revision + 1)
        {
            problems.Add(Problem($"the registry has revision {merged.Revision} of this pack, so the next one is {merged.Revision + 1}, not {revision.Revision}."));
        }

        var kept = revision.Revisions.Take(merged.Revisions.Count).ToList();
        if (kept.Count < merged.Revisions.Count || !kept.SequenceEqual(merged.Revisions))
        {
            problems.Add(Problem($"revisions 1–{merged.Revision} must stay as the registry has them; a new revision only adds one."));
        }

        if (revision.Created != merged.Created)
        {
            problems.Add(Problem("created must stay the date the pack was first published."));
        }

        var logins = revision.Authors.Select(a => a.GitHub).ToList();
        if (merged.Authors.Where((author, i) => i >= logins.Count || !string.Equals(logins[i], author.GitHub, StringComparison.OrdinalIgnoreCase)).Any())
        {
            problems.Add(Problem($"the pack's authors ({string.Join(", ", merged.Authors.Select(a => a.GitHub))}) must stay first, in their order; a new revision adds its author after them."));
        }

        return problems;
    }

    private static PackProblem Problem(string message) => new(string.Empty, ProblemField, message);
}
