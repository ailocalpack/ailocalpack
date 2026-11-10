using System.Globalization;
using System.Text;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Publishing;

/// <summary>What a local pack becomes in the registry.</summary>
public abstract record PublishTarget
{
    private PublishTarget()
    {
    }

    /// <summary>A pack of its own (its local ID, or the first free <c>&lt;id&gt;-2</c>… when the registry has it).</summary>
    public sealed record NewPack : PublishTarget;

    /// <summary>The next revision of the registry pack <paramref name="PackId"/> of the same game and language.</summary>
    public sealed record RevisionOf(string PackId) : PublishTarget;
}

/// <summary>A registry pack of the same game + language as the pack being published, for the "similar pack" choice.</summary>
/// <param name="IsBase">The local pack is an update (task 56) of this pack.</param>
/// <param name="RevisionRecommended">A new revision of it is the better choice (see <see cref="SimilarPacks.Find"/>).</param>
public sealed record SimilarPack(RegistryPack Pack, bool IsBase, bool RevisionRecommended);

/// <summary>Registry packs a local pack could be a new revision of (the duplicate check before publishing).</summary>
public static class SimilarPacks
{
    /// <summary>
    /// The index's packs of the local pack's game + language, the one it was made from first, then by 👍 and ID. A new
    /// revision is recommended (decided) when the local pack is an update of it, when it doesn't support the player's
    /// Steam build (made for an older or other one), or when it translates a smaller share of the strings.
    /// </summary>
    public static IReadOnlyList<SimilarPack> Find(RegistryIndex index, LocalPack local)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(local);
        var manifest = local.Manifest;
        var buildIds = manifest.Builds.Select(b => b.BuildId).OfType<string>().ToList();
        return
        [
            .. index.PacksFor(manifest.Game, manifest.Language)
                .Select(pack =>
                {
                    var isBase = local.Report.Update is not null && pack.Id == manifest.Id;
                    var otherBuild = buildIds.Count > 0 && !buildIds.Any(pack.BuildIds.Contains);
                    var smaller = pack.Coverage.Ratio < manifest.Coverage.Ratio;
                    return new SimilarPack(pack, isBase, isBase || otherBuild || smaller);
                })
                .OrderByDescending(s => s.IsBase)
                .ThenByDescending(s => s.Pack.Rating.Likes)
                .ThenBy(s => s.Pack.Id, StringComparer.Ordinal),
        ];
    }
}

/// <summary>A file of the pack folder as it is committed (path relative to the pack folder, <c>/</c>-separated).</summary>
public sealed record PublishFile(string Path, byte[] Content);

/// <summary>Everything committed for one publish: the registry manifest, the files, where they go, the PR's text.</summary>
/// <param name="Existing">The registry pack's manifest this is a new revision of, or <c>null</c> for a new pack.</param>
/// <param name="PatchArchiveName">The Release asset name of a patch pack's archive.</param>
public sealed record PublishPayload(
    PackManifest Manifest,
    PackManifest? Existing,
    IReadOnlyList<PublishFile> Files,
    string? PatchArchiveName)
{
    public bool IsRevision => Existing is not null;

    /// <summary>The pack folder in the registry: <c>games/&lt;game folder&gt;/&lt;language&gt;/&lt;id&gt;</c>.</summary>
    public string Folder => PublishPayloads.FolderOf(Manifest);

    /// <summary>The fork's branch: <c>pack/&lt;game folder&gt;/&lt;language&gt;/&lt;id&gt;/rev-&lt;N&gt;</c> (a resubmit pushes to it again).</summary>
    public string Branch => $"pack/{RegistryLayout.GameFolder(Manifest.Game)}/{Manifest.Language}/{Manifest.Id}/rev-{Manifest.Revision.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Builds the registry manifest of a local pack (pure). The placeholder author (<see cref="LocalPackStore.PlaceholderAuthor"/>)
/// becomes the player; the content (agent, builds, recipe or patch, string table, fonts, dependencies, coverage) is the
/// local pack's; the status is <c>alpha</c>; a patch pack's URL names the registry's Release asset for the published ID
/// and revision. A <b>new pack</b> starts at revision 1 by the player (other authors the local pack credits, e.g. the
/// base of an update, stay credited after the player). A <b>new revision</b> keeps the existing pack's ID, title,
/// created date, authors (the original author first) and history, adds the player as an author when new (revision
/// credit) and one revision by the player.
/// </summary>
public static class PublishPayloads
{
    public static string FolderOf(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return $"{RegistryLayout.GamesFolder}/{RegistryLayout.GameFolder(manifest.Game)}/{manifest.Language}/{manifest.Id}";
    }

    /// <summary><paramref name="preferred"/> when not taken, else the first free <c>&lt;preferred&gt;-2</c>, <c>-3</c>… (at most 64 characters).</summary>
    public static string FreeId(string preferred, IReadOnlyCollection<string> taken)
    {
        ArgumentException.ThrowIfNullOrEmpty(preferred);
        ArgumentNullException.ThrowIfNull(taken);
        var id = preferred;
        for (var n = 2; taken.Contains(id); n++)
        {
            var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            id = preferred[..Math.Min(preferred.Length, 64 - suffix.Length)].TrimEnd('-') + suffix;
        }

        return id;
    }

    public static PackManifest NewPack(PackManifest local, PackAuthor player, string id, DateTimeOffset now, RegistryRepo registry)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(registry);
        var date = Whole(now);
        var authors = new List<PackAuthor> { player };
        authors.AddRange(Credits(local, player));
        var manifest = local with
        {
            Id = id,
            Authors = authors,
            Revisions = [new PackRevision(1, date, player.GitHub, LocalNotes(local))],
            Status = PackStatus.Alpha,
            Created = date,
            Updated = date,
        };
        return WithPatchUrl(manifest, registry);
    }

    public static PackManifest Revision(PackManifest local, PackManifest existing, PackAuthor player, DateTimeOffset now, RegistryRepo registry)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(registry);
        var date = new[] { Whole(now), existing.Updated, existing.Revisions[^1].Date }.Max();
        var authors = existing.Authors.ToList();
        foreach (var author in Credits(local, player).Prepend(player))
        {
            if (!authors.Any(a => SameLogin(a.GitHub, author.GitHub)))
            {
                authors.Add(author);
            }
        }

        var manifest = local with
        {
            Id = existing.Id,
            Title = existing.Title,
            Authors = authors,
            Revisions = [.. existing.Revisions, new PackRevision(existing.Revision + 1, date, player.GitHub, LocalNotes(local))],
            Status = PackStatus.Alpha,
            Created = existing.Created,
            Updated = date,
        };
        return WithPatchUrl(manifest, registry);
    }

    /// <summary>The commit's message.</summary>
    public static string CommitMessage(PublishPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var m = payload.Manifest;
        return payload.IsRevision
            ? $"{m.Title}: revision {m.Revision} ({m.Language}, {m.Game})"
            : $"{m.Title}: new pack ({m.Language}, {m.Game})";
    }

    /// <summary>The pull request's title.</summary>
    public static string PullRequestTitle(PublishPayload payload, string? gameName)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var m = payload.Manifest;
        var game = string.IsNullOrWhiteSpace(gameName) ? m.Game.ToString() : gameName.Trim();
        return payload.IsRevision
            ? $"Revision {m.Revision} of «{m.Title}» · {m.Language} · {game}"
            : $"New pack «{m.Title}» · {m.Language} · {game}";
    }

    /// <summary>The pull request's body (Markdown): what the pack is, how it was checked, the license statement.</summary>
    /// <param name="forkAsset">Where registry CI takes a patch pack's archive from (the asset in the player's fork).</param>
    public static string PullRequestBody(PublishPayload payload, LocalPackReport? report, string? gameName, Uri? forkAsset)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var m = payload.Manifest;
        var body = new StringBuilder();
        var game = string.IsNullOrWhiteSpace(gameName) ? $"`{m.Game}`" : $"{gameName.Trim()} (`{m.Game}`)";
        body.Append(CultureInfo.InvariantCulture, $"{(payload.IsRevision ? $"Revision {m.Revision} of «{m.Title}» by @{m.Author.GitHub}" : $"New pack «{m.Title}»")} for {game} in `{m.Language}`, ");
        body.Append(CultureInfo.InvariantCulture, $"published with AI Local Pack by @{m.Revisions[^1].By}.\n\n");
        body.Append("| | |\n|---|---|\n");
        Row(body, "Pack", $"`{payload.Folder}`");
        Row(body, "Revision", m.Revision.ToString(CultureInfo.InvariantCulture) + (m.Revisions[^1].Notes is { } notes ? $" ({notes})" : ""));
        Row(body, "Kind", m.Patch is { } patch
            ? $"binary patch of {Files(patch.Files.Count)} ({patch.Size.ToString(CultureInfo.InvariantCulture)} bytes)"
            : $"strings + recipe `{m.Recipe?.Tool}` {m.Recipe?.Version}");
        Row(body, "Coverage", $"{m.Coverage.Translated.ToString(CultureInfo.InvariantCulture)} of {m.Coverage.Total.ToString(CultureInfo.InvariantCulture)} strings ({(m.Coverage.Ratio * 100).ToString("0.#", CultureInfo.InvariantCulture)}%)");
        Row(body, "Agent", m.Agent is { } agent ? string.Join(" ", new[] { agent.Id, agent.Version, agent.Model is null ? null : $"({agent.Model})" }.OfType<string>()) : "made by hand");
        Row(body, "Game build", string.Join(", ", m.Builds.Select(b => b.BuildId is { } id ? $"{id} ({Files(b.Files.Count)})" : Files(b.Files.Count))));
        if (report is not null)
        {
            Row(body, "Test run", report.TestRun.Outcome switch
            {
                TestRunOutcome.Passed => "passed (the game kept running with the pack)",
                TestRunOutcome.Skipped => $"skipped{(report.TestRun.Detail is { } detail ? $" ({detail})" : "")}",
                _ => report.TestRun.Outcome.ToString().ToLowerInvariant(),
            });
            Row(body, "Tried in game", report.IsTried ? "yes" : "no");
        }

        Row(body, "Fonts", m.Fonts.Count == 0 ? "the game's own" : string.Join(", ", m.Fonts.Select(f => $"`{f}`")));
        if (m.Dependencies.Count > 0)
        {
            Row(body, "Dependencies", string.Join(", ", m.Dependencies.Select(d => $"`{d.Id}` {d.Version}")));
        }

        if (m.Patch is { } archive)
        {
            body.Append(CultureInfo.InvariantCulture, $"\nThe patch archive `{payload.PatchArchiveName}` (sha256 `{archive.Sha256}`) is a Release asset of the author's fork");
            body.Append(forkAsset is null ? "" : $": {forkAsset}");
            body.Append(CultureInfo.InvariantCulture, $". Registry CI checks it against the manifest and copies it to this repository's `{RegistryRepo.PatchReleaseTag}` release.\n");
        }

        body.Append("\nThe pack holds translations and a recipe only: no original text, no game files, nothing that runs. ");
        body.Append(CultureInfo.InvariantCulture, $"It is published under {PackManifest.License}. CI checks it and merges it as `alpha`.\n");
        return body.ToString();
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count.ToString(CultureInfo.InvariantCulture)} files";

    private static void Row(StringBuilder body, string name, string value) =>
        body.Append(CultureInfo.InvariantCulture, $"| {name} | {value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)} |\n");

    private static PackManifest WithPatchUrl(PackManifest manifest, RegistryRepo registry) =>
        manifest.Patch is { } patch
            ? manifest with { Patch = patch with { Url = registry.PatchUrl(LocalPackStore.PatchArchiveName(manifest.Game, manifest.Language, manifest.Id, manifest.Revision)) } }
            : manifest;

    /// <summary>The local pack's authors other than the placeholder and the player (an update's base authors).</summary>
    private static IEnumerable<PackAuthor> Credits(PackManifest local, PackAuthor player) =>
        local.Authors.Where(a => !SameLogin(a.GitHub, LocalPackStore.PlaceholderAuthor) && !SameLogin(a.GitHub, player.GitHub));

    /// <summary>The notes of the local pack's own revision (an update's "12 new strings…"), when it made one.</summary>
    private static string? LocalNotes(PackManifest local) =>
        SameLogin(local.Revisions[^1].By, LocalPackStore.PlaceholderAuthor) ? local.Revisions[^1].Notes : null;

    private static bool SameLogin(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset Whole(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}
