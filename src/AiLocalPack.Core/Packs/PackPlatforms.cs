using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Packs;

/// <summary>
/// The platforms a new revision would take away from players of the existing one (task 87): <paramref name="Revision"/>
/// is the existing revision's number, <paramref name="Existing"/> what it supports, <paramref name="Kept"/> what the new
/// revision supports, <paramref name="Lost"/> the existing ones it doesn't (never empty).
/// </summary>
public sealed record PlatformLoss(int Revision, IReadOnlyList<OsKind> Existing, IReadOnlyList<OsKind> Kept, IReadOnlyList<OsKind> Lost)
{
    /// <summary>"Windows, macOS".</summary>
    public static string Names(IEnumerable<OsKind> platforms) => string.Join(", ", (platforms ?? []).Select(PackPlatforms.DisplayName));

    /// <summary>The English sentence the publish client, the CLI and registry CI say.</summary>
    public string Describe() =>
        $"rev {Revision} supports {Names(Existing)}; this revision would support only {Names(Kept)}: {Names(Lost)} players would lose a compatible translation.";
}

/// <summary>
/// Which platforms a pack supports (pure; task 87). A Steam build ID is the same on every OS while its files are not, so
/// each manifest build records its <c>os</c>; a build without one (a pack from before platforms were recorded) matches
/// on any platform, so it counts as every platform.
/// </summary>
public static class PackPlatforms
{
    public static IReadOnlyList<OsKind> All { get; } = [OsKind.Windows, OsKind.MacOS, OsKind.Linux];

    /// <summary>"Windows", "macOS", "Linux": the platforms' own names, the same in every language.</summary>
    public static string DisplayName(OsKind os) => os switch
    {
        OsKind.Windows => "Windows",
        OsKind.Linux => "Linux",
        _ => "macOS",
    };

    /// <summary>The platforms of a manifest's builds, in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<OsKind> Of(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Of(manifest.Builds);
    }

    /// <inheritdoc cref="Of(PackManifest)"/>
    public static IReadOnlyList<OsKind> Of(IEnumerable<PackBuild> builds)
    {
        ArgumentNullException.ThrowIfNull(builds);
        var list = builds.ToList();
        return list.Any(b => b.Os is null) ? All : [.. All.Where(os => list.Any(b => b.Os == os))];
    }

    /// <summary>
    /// The platforms of an index pack (its <c>buildIds</c> + <c>buildOs</c>): a build ID without platforms counts as
    /// every platform; <c>null</c> when the index can't tell (a manually added game's pack lists no build IDs).
    /// </summary>
    public static IReadOnlyList<OsKind>? Of(RegistryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.BuildIds.Count == 0)
        {
            return null;
        }

        var known = pack.BuildIds.Select(id => pack.BuildOs?.GetValueOrDefault(id)).ToList();
        return known.Any(p => p is null) ? All : [.. All.Where(os => known.Any(p => p!.Contains(os)))];
    }

    /// <summary>What <paramref name="revision"/> takes away from players of <paramref name="existing"/>; <c>null</c> = nothing.</summary>
    public static PlatformLoss? Lost(PackManifest existing, PackManifest revision)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(revision);
        return Lost(existing.Revision, Of(existing), Of(revision));
    }

    /// <summary>The field registry CI's annotation is titled with when a revision drops platforms (<c>pack validate --base</c>).</summary>
    public const string ProblemField = "platforms";

    /// <summary>
    /// Registry CI's check of a revision PR against the merged revision of the same pack (task 87): a problem when it
    /// supports fewer platforms; <c>null</c> when it keeps them or is another pack.
    /// </summary>
    public static PackProblem? Problem(PackManifest merged, PackManifest revision)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(revision);
        if (merged.Game != revision.Game || merged.Language != revision.Language || merged.Id != revision.Id
            || Lost(merged, revision) is not { } loss)
        {
            return null;
        }

        return new PackProblem(string.Empty, ProblemField, $"{loss.Describe()} Keep the other platforms' builds, or publish it as a separate pack (a maintainer can still merge it).");
    }

    /// <summary>What a revision supporting <paramref name="kept"/> takes away from <paramref name="existing"/>; <c>null</c> = nothing.</summary>
    public static PlatformLoss? Lost(int revision, IReadOnlyList<OsKind> existing, IReadOnlyList<OsKind> kept)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(kept);
        var lost = existing.Where(os => !kept.Contains(os)).ToList();
        return lost.Count == 0 ? null : new PlatformLoss(revision, existing, kept, lost);
    }

    /// <summary>
    /// The existing revision's builds a new revision keeps (decided, task 87): only between two strings + recipe packs of
    /// the same recipe tool whose new string table still has every key the existing one translates (else the old
    /// platform's players would get a translation missing strings it had, or one made for other files); then every
    /// existing build on a platform the local pack has no build for, and every build without a platform (it matched any
    /// platform and still does), each build ID + platform once. The local pack's builds win on their own platforms.
    /// </summary>
    public static IReadOnlyList<PackBuild> CarriedOver(PackManifest local, PackManifest existing, StringTable? localStrings, StringTable? existingStrings)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(existing);
        if (local.Patch is not null || existing.Patch is not null
            || local.Recipe is null || existing.Recipe is null
            || local.Recipe.Tool != existing.Recipe.Tool
            || localStrings is null || existingStrings is null
            || !existingStrings.Strings.Keys.All(localStrings.Strings.ContainsKey))
        {
            return [];
        }

        var localOs = local.Builds.Select(b => b.Os).OfType<OsKind>().ToHashSet();
        var taken = local.Builds.Select(Key).ToHashSet();
        var kept = new List<PackBuild>();
        foreach (var build in existing.Builds)
        {
            if ((build.Os is null || !localOs.Contains(build.Os.Value)) && (build.BuildId is null || taken.Add(Key(build))))
            {
                kept.Add(build);
            }
        }

        return kept;
    }

    private static (string?, OsKind?) Key(PackBuild build) => (build.BuildId, build.Os);
}
