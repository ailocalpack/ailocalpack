using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Registry;

/// <summary>One problem met while building the index.</summary>
/// <param name="File">Relative to the registry root, '/'-separated (<c>games/steam-620/ru/full/manifest.json</c>); empty for the root.</param>
public sealed record RegistryBuildProblem(string File, string Field, string Message)
{
    public override string ToString() => string.Join(": ", new[] { File, Field, Message }.Where(p => p.Length > 0));
}

/// <summary>Result of <see cref="RegistryIndexBuilder.Build"/>.</summary>
/// <param name="Index">The index, or <c>null</c> when it can't be built (missing root, broken policy or ratings file).</param>
/// <param name="Problems">
/// With an <paramref name="Index"/>: what was left out (packs that fail <see cref="PackValidator"/>, stray files, ratings
/// of packs that don't exist). Without one: why nothing was built.
/// </param>
public sealed record RegistryBuildResult(RegistryIndex? Index, IReadOnlyList<RegistryBuildProblem> Problems);

/// <summary>
/// Builds <c>index.json</c> from a registry checkout: every pack under <c>games/&lt;game&gt;/&lt;language&gt;/&lt;id&gt;/</c>
/// that passes <see cref="PackValidator"/> (a failing one is left out, the rest published), game flags from
/// <see cref="RegistryOptOuts.Path"/> and the <c>allowed</c> list of <see cref="AntiCheatPolicyPath"/>, the dependency
/// whitelist from <see cref="DependencyWhitelist.PolicyPath"/> (packs naming anything else are left out), ratings and
/// discussion links from the ratings file. Manifest paths are relative to the root, where <c>index.json</c> is published.
/// </summary>
public static class RegistryIndexBuilder
{
    public const string AntiCheatPolicyPath = "policy/anti-cheat.json";

    public static RegistryBuildResult Build(
        string root,
        DateTimeOffset generated,
        IReadOnlyDictionary<RegistryPackKey, RegistryPackRating>? ratings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var problems = new List<RegistryBuildProblem>();
        if (!Directory.Exists(root))
        {
            return Fail("", "The registry folder doesn't exist.");
        }

        if (!ReadPolicy(root, RegistryOptOuts.Path, RegistryOptOuts.Read, problems, out var optOuts)
            || !ReadPolicy(root, AntiCheatPolicyPath, ReadAllowed, problems, out var allowed))
        {
            return new RegistryBuildResult(null, problems);
        }

        if (!ReadDependencies(root, problems, out var dependencies))
        {
            return new RegistryBuildResult(null, problems);
        }

        var packs = new List<RegistryPack>();
        var games = Path.Combine(root, RegistryLayout.GamesFolder);
        if (Directory.Exists(games))
        {
            foreach (var folder in PackFolders(root, games, problems))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validation = PackValidator.Validate(folder, dependencies, cancellationToken);
                var relative = Relative(root, folder);
                if (!validation.IsValid)
                {
                    problems.AddRange(validation.Problems.Select(p =>
                        new RegistryBuildProblem(p.File.Length == 0 ? relative : $"{relative}/{p.File}", p.Field, $"{p.Message} (pack left out)")));
                    continue;
                }

                var manifest = validation.Manifest!;
                var key = new RegistryPackKey(manifest.Game, manifest.Language, manifest.Id);
                var rating = ratings?.GetValueOrDefault(key);
                packs.Add(ToIndexPack(manifest, $"{relative}/{RegistryLayout.ManifestFileName}", rating));
            }
        }

        if (ratings is not null)
        {
            var listed = packs.Select(p => new RegistryPackKey(p.Game, p.Language, p.Id)).ToHashSet();
            problems.AddRange(ratings.Keys.Where(k => !listed.Contains(k)).OrderBy(k => k.ToString(), StringComparer.Ordinal)
                .Select(k => new RegistryBuildProblem("", "", $"ratings for {k}, which is not in the registry, are left out.")));
        }

        var ids = packs.Select(p => p.Game).Concat(optOuts!).Concat(allowed!).Distinct();
        var index = new RegistryIndex(
            generated,
            ids.ToDictionary(
                id => id,
                id => new RegistryGame(
                    id,
                    optOuts!.Contains(id),
                    allowed!.Contains(id),
                    packs.Where(p => p.Game == id).GroupBy(p => p.Language, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, IReadOnlyList<RegistryPack> (g) => [.. g.OrderBy(p => p.Id, StringComparer.Ordinal)]))));
        return new RegistryBuildResult(index with { Dependencies = dependencies! }, problems);
    }

    /// <summary><see cref="DependencyWhitelist.PolicyPath"/>: missing = empty; unreadable or invalid stops the build.</summary>
    private static bool ReadDependencies(string root, List<RegistryBuildProblem> problems, out DependencyWhitelist? whitelist)
    {
        whitelist = DependencyWhitelist.Empty;
        var file = Path.Combine(root, DependencyWhitelist.PolicyPath);
        if (!File.Exists(file))
        {
            return true;
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(new RegistryBuildProblem(DependencyWhitelist.PolicyPath, "", $"can't be read: {e.Message}"));
            return false;
        }

        (whitelist, var errors) = DependencyWhitelist.Parse(text);
        problems.AddRange(errors.Select(e => new RegistryBuildProblem(DependencyWhitelist.PolicyPath, e.Field, e.Message)));
        return whitelist is not null;
    }

    private static RegistryPack ToIndexPack(PackManifest manifest, string manifestPath, RegistryPackRating? rating) => new(
        manifest.Id,
        manifest.Game,
        manifest.Language,
        manifest.Title,
        manifest.Author,
        manifest.Status,
        manifest.Revision,
        manifest.Updated,
        manifest.Coverage,
        [.. manifest.Builds.Select(b => b.BuildId).OfType<string>()],
        manifest.Builds.Count,
        manifestPath,
        (manifest.Strings?.Size ?? 0) + (manifest.Patch?.Size ?? 0),
        rating?.Rating ?? RegistryRating.None,
        rating?.Discussion);

    /// <summary>The <c>games/&lt;game&gt;/&lt;language&gt;/&lt;id&gt;</c> folders in ordinal order; files on the way are reported.</summary>
    private static List<string> PackFolders(string root, string games, List<RegistryBuildProblem> problems)
    {
        var result = new List<string>();
        void Walk(string folder, int depth)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add(new RegistryBuildProblem(Relative(root, folder), "", $"can't be read: {e.Message}"));
                return;
            }

            foreach (var entry in entries)
            {
                if (entry is not DirectoryInfo || entry.LinkTarget is not null)
                {
                    problems.Add(new RegistryBuildProblem(Relative(root, entry.FullName), "", "is not part of the games/<game>/<language>/<pack id>/ layout; left out."));
                }
                else if (depth == 2)
                {
                    result.Add(entry.FullName);
                }
                else
                {
                    Walk(entry.FullName, depth + 1);
                }
            }
        }

        Walk(games, 0);
        return result;
    }

    private static (IReadOnlySet<GameId>? Games, IReadOnlyList<PackFormatError> Errors) ReadAllowed(string json)
    {
        try
        {
            return (AntiCheatPolicy.Parse(json).AllowedGames, []);
        }
        catch (AntiCheatPolicyException e)
        {
            return (null, [new PackFormatError("", e.Message)]);
        }
    }

    /// <summary>A missing file is an empty list; an unreadable or invalid one stops the build.</summary>
    private static bool ReadPolicy(
        string root,
        string path,
        Func<string, (IReadOnlySet<GameId>? Games, IReadOnlyList<PackFormatError> Errors)> read,
        List<RegistryBuildProblem> problems,
        out IReadOnlySet<GameId>? games)
    {
        games = new HashSet<GameId>();
        var file = Path.Combine(root, path);
        if (!File.Exists(file))
        {
            return true;
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(new RegistryBuildProblem(path, "", $"can't be read: {e.Message}"));
            return false;
        }

        (games, var errors) = read(text);
        problems.AddRange(errors.Select(e => new RegistryBuildProblem(path, e.Field, e.Message)));
        return games is not null;
    }

    private static RegistryBuildResult Fail(string file, string message) => new(null, [new RegistryBuildProblem(file, "", message)]);

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }
}
