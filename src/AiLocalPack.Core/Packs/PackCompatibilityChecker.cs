using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Packs;

/// <summary>
/// Decides whether a pack fits the player's copy of a game, in this order:
/// <list type="number">
/// <item>pack for another game, game folder unreachable, recipe tool unknown or too old in this app → incompatible;</item>
/// <item>the game's store build ID is listed → compatible if the files of that build are all there (no hashing),
/// else incompatible;</item>
/// <item>otherwise the touched files are hashed (only those, through the <see cref="FileHashCache"/>) and compared with
/// every listed build whose files are all present: one build matching exactly → compatible; else, against the closest
/// build, a recipe pack is probably compatible (the recipe rebuilds the changed files from the strings of the player's
/// own copy) and a binary patch is incompatible (it applies only to the exact files it was made from);</item>
/// <item>no listed build has all its files → incompatible (files missing).</item>
/// </list>
/// Whether the strings of changed files still match the pack's keys is found out when the recipe runs at install (task 28).
/// Never throws for disk state: unreadable files become <see cref="PackCompatibilityReason.FileUnreadable"/>.
/// </summary>
public sealed class PackCompatibilityChecker
{
    private readonly FileHashCache _hashes;
    private readonly IRecipeTools? _recipes;

    /// <param name="hashes">Where file hashes come from (and are remembered).</param>
    /// <param name="recipes">The toolkit's recipe tools; <c>null</c> = recipes not checked.</param>
    public PackCompatibilityChecker(FileHashCache hashes, IRecipeTools? recipes = null)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        _hashes = hashes;
        _recipes = recipes;
    }

    public PackCompatibility Check(PackManifest pack, Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(game);

        IReadOnlyList<string> packBuildIds = [.. pack.Builds.Select(b => b.BuildId).OfType<string>()];
        var gameBuildId = game.Version?.BuildId;
        var touchedCount = pack.TouchedFiles.Count;

        PackCompatibility Result(
            PackCompatibilityVerdict verdict,
            PackCompatibilityReason reason,
            PackBuild? reference = null,
            IReadOnlyList<string>? changed = null,
            IReadOnlyList<string>? missing = null,
            string? unreadable = null,
            PackRecipe? recipe = null,
            string? engine = null) =>
            new(
                verdict,
                reason,
                packBuildIds,
                gameBuildId,
                reference?.BuildId,
                reference?.Files.Count ?? touchedCount,
                changed ?? [],
                missing ?? [],
                unreadable,
                recipe,
                engine);

        if (pack.Game != game.Id)
        {
            return Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.WrongGame);
        }

        if (!game.IsAvailable || !Directory.Exists(game.InstallPath))
        {
            return Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.GameNotAvailable);
        }

        if (pack.Recipe is { } recipe && _recipes?.Check(recipe, game.Engine) is { } support and not RecipeToolSupport.Supported)
        {
            var reason = support switch
            {
                RecipeToolSupport.UnknownTool => PackCompatibilityReason.RecipeToolUnknown,
                RecipeToolSupport.EngineUnsupported => PackCompatibilityReason.EngineUnsupported,
                _ => PackCompatibilityReason.RecipeToolTooNew,
            };
            return Result(PackCompatibilityVerdict.Incompatible, reason, recipe: recipe, engine: game.Engine);
        }

        var root = Path.GetFullPath(game.InstallPath);
        var missing = pack.TouchedFiles.Where(f => !File.Exists(GamePath(root, f))).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> MissingOf(PackBuild build) =>
            [.. build.Files.Keys.Where(missing.Contains).Order(StringComparer.Ordinal)];

        if (gameBuildId is not null && pack.Builds.FirstOrDefault(b => b.BuildId == gameBuildId) is { } listed)
        {
            var listedMissing = MissingOf(listed);
            return listedMissing.Count == 0
                ? Result(PackCompatibilityVerdict.Compatible, PackCompatibilityReason.BuildListed, listed)
                : Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.FilesMissing, listed, missing: listedMissing);
        }

        var complete = pack.Builds.Where(b => !b.Files.Keys.Any(missing.Contains)).ToList();
        if (complete.Count == 0)
        {
            // Later builds win ties: they are closer to what players have now.
            var closest = pack.Builds.Select((b, i) => (Build: b, Index: i, Missing: MissingOf(b)))
                .OrderBy(x => x.Missing.Count).ThenByDescending(x => x.Index).First();
            return Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.FilesMissing, closest.Build, missing: closest.Missing);
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in complete.SelectMany(b => b.Files.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            try
            {
                hashes[file] = _hashes.Hash(GamePath(root, file), cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.FileUnreadable, unreadable: file);
            }
        }

        var best = complete.Select((b, i) => (
                Build: b,
                Index: i,
                Changed: (IReadOnlyList<string>)[.. b.Files.Where(f => hashes[f.Key] != f.Value).Select(f => f.Key).Order(StringComparer.Ordinal)]))
            .OrderBy(x => x.Changed.Count).ThenByDescending(x => x.Index).First();

        if (best.Changed.Count == 0)
        {
            return Result(PackCompatibilityVerdict.Compatible, PackCompatibilityReason.FilesMatch, best.Build);
        }

        return pack.Patch is not null
            ? Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.PatchFilesChanged, best.Build, best.Changed)
            : Result(PackCompatibilityVerdict.ProbablyCompatible, PackCompatibilityReason.FilesChanged, best.Build, best.Changed);
    }

    /// <summary>
    /// Full path of a manifest path ('/'-separated, relative) inside the game folder. A path that would leave the folder
    /// (a drive-rooted part on Windows) points at a name that can't exist, so it counts as missing.
    /// </summary>
    private static string GamePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(root, "\0outside");
    }
}
