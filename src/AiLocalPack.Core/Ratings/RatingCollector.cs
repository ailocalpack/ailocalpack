using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Ratings;

/// <summary>One pack's line in the collector's log.</summary>
/// <param name="Created">Its Discussion was opened by this run.</param>
public sealed record CollectedPack(RatedPack Pack, Uri? Discussion, bool Created, PackTally Tally);

/// <summary>What <see cref="RatingCollector.CollectAsync"/> gathered: the ratings file's content, per pack, and warnings.</summary>
public sealed record RatingCollection(
    IReadOnlyDictionary<RegistryPackKey, RegistryPackRating> Ratings,
    IReadOnlyList<CollectedPack> Packs,
    IReadOnlyList<RegistryBuildProblem> Warnings);

/// <summary>
/// <c>ratings collect</c> (task 64): reads the packs of a registry checkout (their manifests: current revision,
/// authors, builds), every Discussion of the registry's <see cref="Category"/> category, ties them by
/// <see cref="PackDiscussionMarker"/> (several for one pack: the oldest, the rest reported), opens a Discussion for a
/// pack without one when asked, and counts each pack's rating with <see cref="RatingTally"/>. Fails as a whole on any
/// GitHub error, so a half-read registry never publishes lower numbers.
/// </summary>
public sealed class RatingCollector(RegistryDiscussionClient client)
{
    /// <summary>The Discussion category of packs in the registry (task 63).</summary>
    public const string Category = "Packs";

    public async Task<RatingResult<RatingCollection>> CollectAsync(
        string root,
        RegistryRepo repo,
        bool createMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(repo);
        var warnings = new List<RegistryBuildProblem>();
        var packs = ReadPacks(root, warnings);

        var category = await client.CategoryAsync(repo, Category, cancellationToken).ConfigureAwait(false);
        if (!category.IsOk)
        {
            return RatingResult<RatingCollection>.Failed(category.Error);
        }

        if (category.Value.CategoryId is not { } categoryId)
        {
            return RatingResult<RatingCollection>.Failed(new RatingError(
                RatingProblem.NotFound, $"{repo.FullName} has no Discussion category \"{Category}\" (made by hand in the repository settings)."));
        }

        var listed = await client.ListAsync(repo, categoryId, cancellationToken).ConfigureAwait(false);
        if (!listed.IsOk)
        {
            return RatingResult<RatingCollection>.Failed(listed.Error);
        }

        var byPack = new Dictionary<RegistryPackKey, CollectedDiscussion>();
        foreach (var discussion in listed.Value.Where(d => d.Pack is not null).OrderBy(d => d.Number))
        {
            if (!byPack.TryAdd(discussion.Pack!, discussion))
            {
                warnings.Add(new RegistryBuildProblem("", "", $"{discussion.Url} is another Discussion of {discussion.Pack}; only {byPack[discussion.Pack!].Url} counts."));
            }
        }

        var ratings = new Dictionary<RegistryPackKey, RegistryPackRating>();
        var collected = new List<CollectedPack>();
        foreach (var pack in packs)
        {
            var created = false;
            var discussion = byPack.GetValueOrDefault(pack.Key);
            if (discussion is null && createMissing)
            {
                var made = await client.CreateAsync(category.Value, PackDiscussionText.Title(pack), PackDiscussionText.Body(pack, repo), cancellationToken).ConfigureAwait(false);
                if (!made.IsOk)
                {
                    return RatingResult<RatingCollection>.Failed(made.Error with { Message = $"Can't open a Discussion for {pack.Key}: {made.Error.Message}" });
                }

                discussion = new CollectedDiscussion(made.Value.Id, made.Value.Number, made.Value.Url, pack.Key, [], []);
                created = true;
            }

            var tally = RatingTally.Count(pack, discussion);
            collected.Add(new CollectedPack(pack, discussion?.Url, created, tally));
            if (discussion is not null || tally.Rating != RegistryRating.None)
            {
                ratings[pack.Key] = new RegistryPackRating(tally.Rating, discussion?.Url, pack.Revision);
            }
        }

        var known = packs.Select(p => p.Key).ToHashSet();
        warnings.AddRange(byPack.Where(d => !known.Contains(d.Key)).OrderBy(d => d.Value.Number)
            .Select(d => new RegistryBuildProblem("", "", $"{d.Value.Url} is the Discussion of {d.Key}, which is not in the registry.")));
        return RatingResult<RatingCollection>.Ok(new RatingCollection(ratings, collected, warnings));
    }

    /// <summary>The packs of the checkout whose manifest reads; the others are reported (index build leaves them out too).</summary>
    public static IReadOnlyList<RatedPack> ReadPacks(string root, List<RegistryBuildProblem> warnings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(warnings);
        var games = Path.Combine(root, RegistryLayout.GamesFolder);
        if (!Directory.Exists(games))
        {
            return [];
        }

        var packs = new List<RatedPack>();
        foreach (var folder in RegistryIndexBuilder.PackFolders(root, games, warnings))
        {
            var file = Path.Combine(folder, RegistryLayout.ManifestFileName);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            PackReadResult<PackManifest> read;
            try
            {
                read = PackManifestSerializer.Read(File.ReadAllText(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new RegistryBuildProblem(relative, "", $"can't be read: {e.Message}; left out."));
                continue;
            }

            if (read.Value is not { } manifest)
            {
                warnings.Add(new RegistryBuildProblem(relative, "", "is not a valid manifest; left out."));
                continue;
            }

            var builds = manifest.Builds.Select(b => b.BuildId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            packs.Add(new RatedPack(
                new RegistryPackKey(manifest.Game, manifest.Language, manifest.Id),
                manifest.Title,
                manifest.Revision,
                [.. manifest.Authors.Select(a => a.GitHub)],
                manifest.Game.Kind == Games.GameIdKind.Steam ? builds : null));
        }

        return packs;
    }
}

/// <summary>The title and body of a Discussion the collector opens (English: players of every language share it).</summary>
public static class PackDiscussionText
{
    public static string Title(RatedPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return $"{pack.Title} · {pack.Key}";
    }

    public static string Body(RatedPack pack, RegistryRepo repo)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(repo);
        var folder = $"{RegistryLayout.GamesFolder}/{RegistryLayout.GameFolder(pack.Key.Game)}/{pack.Key.Language}/{pack.Key.Id}";
        return $"""
            {PackDiscussionMarker.Format(pack.Key)}
            **{pack.Title}**: a community translation of `{pack.Key.Game}` into `{pack.Key.Language}`, installed and rated in {AppInfo.DisplayName}.

            - 👍 this post if you like the pack.
            - Played with it? Rate it in the app (game page → Packs → *Your rating*): **Works** / **Doesn't work** is posted here as a comment. Once {RegistryVerification.RequiredConfirmations} players who aren't its authors confirm a revision works, the pack becomes **verified**.
            - Problems, ideas and reviews are welcome in the comments too.

            Pack files: [{folder}](https://github.com/{repo.FullName}/tree/HEAD/{folder})

            """;
    }
}
