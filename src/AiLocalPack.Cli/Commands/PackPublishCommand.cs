using System.CommandLine;
using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>pack publish &lt;local pack&gt; [--registry &lt;owner/repo&gt;] [--new | --revision-of &lt;id&gt;]</c> (task 60):
/// sends a local pack (its folder as <c>pack local</c> prints it, or <c>&lt;game&gt;/&lt;language&gt;/&lt;id&gt;</c>) to the
/// registry as a pull request from the player's fork, signed in with <c>github login</c>. When the registry index lists
/// packs of the same game + language and neither <c>--new</c> nor <c>--revision-of</c> is given, it lists them (with
/// the recommended one) and stops: exit 1. Publishing the same pack again pushes to the open PR (a fix). Exit 0 =
/// PR opened or updated; 1 = not published (the reason on stderr).
/// </summary>
internal static class PackPublishCommand
{
    public static Command Build(OsEnvironment env)
    {
        var packArgument = new Argument<string>("local-pack") { Description = "The local pack: its folder (as 'pack local' prints it) or <game id>/<language>/<pack id>." };
        var registry = new Option<string>("--registry") { Description = $"The registry repository to open the PR in (default {RegistryRepo.Variable}, else {RegistryRepo.Default}); a test registry for trying it out." };
        var asNew = new Option<bool>("--new") { Description = "Publish as a separate pack even when the registry has packs for this game and language." };
        var revisionOf = new Option<string?>("--revision-of") { Description = "Publish as the next revision of this registry pack (same game and language); its author stays the author, you are credited." };
        var publish = new Command(
            "publish",
            "Publish a local pack to the registry as a GitHub pull request from your fork (sign in first with 'github login'). Publishing it again pushes to the same PR.")
        {
            packArgument, registry, asNew, revisionOf,
        };
        publish.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var repo = RegistryRepo.Resolve(env);
            if (result.GetValue(registry) is { } registryText && !RegistryRepo.TryParse(registryText, out repo))
            {
                await error.WriteLineAsync($"'{registryText}' is not a GitHub repository (<owner>/<repo>).");
                return 1;
            }

            if (result.GetValue(asNew) && result.GetValue(revisionOf) is not null)
            {
                await error.WriteLineAsync("Give --new or --revision-of, not both.");
                return 1;
            }

            var paths = AppPaths.Resolve(env);
            var store = new LocalPackStore(paths);
            if (Find(store, result.GetValue(packArgument)!) is not { } pack)
            {
                await error.WriteLineAsync($"No local pack '{result.GetValue(packArgument)}'; 'ailocalpack pack local' lists them.");
                return 1;
            }

            var manifest = pack.Manifest;
            if (!pack.Report.IsTried)
            {
                await error.WriteLineAsync("warning: the pack hasn't been tried in game yet (the app offers publishing only after that).");
            }

            var index = await ReadIndexAsync(env, paths, error, cancellationToken);
            var similar = index is null ? [] : SimilarPacks.Find(index, pack);
            PublishTarget target;
            if (result.GetValue(revisionOf) is { } id)
            {
                target = new PublishTarget.RevisionOf(id);
            }
            else if (result.GetValue(asNew) || similar.Count == 0)
            {
                target = new PublishTarget.NewPack();
            }
            else
            {
                await error.WriteLineAsync($"The registry already has packs for {manifest.Game} in {manifest.Language}; choose --revision-of <id> (its players get the update) or --new (a separate pack):");
                foreach (var s in similar)
                {
                    var p = s.Pack;
                    await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"  {p.Id}: «{p.Title}» by {p.Author.GitHub}, rev {p.Revision}, {PackManifestSerializer.StatusText(p.Status)}, {p.Coverage.Translated} of {p.Coverage.Total} strings, builds {(p.BuildIds.Count == 0 ? "-" : string.Join(", ", p.BuildIds))}, {p.Rating.Likes} likes{(s.IsBase ? " (your pack is an update of it)" : "")}{(s.RevisionRecommended ? " — recommended: --revision-of " + p.Id : "")}"));
                }

                return 1;
            }

            using var http = GitHubCommand.CreateHttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            var session = GitHubCommand.CreateSession(env, http);
            var publisher = new PackPublisher(session, http, store, paths);
            var steps = new Progress(output, repo);
            var published = await publisher.PublishAsync(new PublishRequest(pack, target, repo, index, GameName(paths, manifest.Game)), steps, cancellationToken);
            switch (published)
            {
                case PublishResult.Published done:
                    var what = $"{done.Manifest.Id} rev {done.Manifest.Revision}";
                    await output.WriteLineAsync(done switch
                    {
                        { Unchanged: true } => $"PR #{done.PullRequest} already has {what}: {done.PullRequestUrl}",
                        { Resubmitted: true } => $"Pushed {what} to the open PR #{done.PullRequest}: {done.PullRequestUrl}",
                        _ => $"Opened PR #{done.PullRequest} for {what}: {done.PullRequestUrl}",
                    });
                    await output.WriteLineAsync($"  branch: {done.Fork}:{done.Branch}");
                    await output.WriteLineAsync("  CI checks the pack and merges it as alpha.");
                    return 0;
                case PublishResult.Failed failed:
                    await error.WriteLineAsync(failed.Message);
                    foreach (var problem in failed.Problems ?? [])
                    {
                        await error.WriteLineAsync($"  {problem}");
                    }

                    return 1;
                default:
                    return 1;
            }
        });
        return publish;
    }

    private static LocalPack? Find(LocalPackStore store, string text)
    {
        var parts = text.Split('/');
        if (parts.Length == 3 && GameId.TryParse(parts[0], out var game))
        {
            return store.Find(game, parts[1], parts[2]);
        }

        var folder = Path.GetFullPath(text).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return store.List().FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Folder), folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The registry index for the duplicate check and the opt-out flag; <c>null</c> (with a warning) when it can't be read.</summary>
    private static async Task<RegistryIndex?> ReadIndexAsync(OsEnvironment env, AppPaths paths, TextWriter error, CancellationToken cancellationToken)
    {
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
        if (!RegistryLocation.TryResolve(env, settings.RegistryIndexUrl, out var location, out var locationError))
        {
            await error.WriteLineAsync($"warning: similar packs not checked: {locationError}");
            return null;
        }

        using var http = IndexCommand.CreateHttpClient();
        var snapshot = await new RegistryIndexClient(http, paths).FetchAsync(location, cancellationToken);
        if (snapshot.Index is null)
        {
            await error.WriteLineAsync($"warning: similar packs not checked: can't read the registry index at {location} ({snapshot.Detail}).");
        }

        return snapshot.Index;
    }

    private static string? GameName(AppPaths paths, GameId game)
    {
        var catalog = new GameCatalog(paths, NullLogger<GameCatalog>.Instance, TimeProvider.System);
        catalog.Load();
        return catalog.Find(game)?.Name;
    }

    private sealed class Progress(TextWriter output, RegistryRepo registry) : IProgress<PublishStep>
    {
        public void Report(PublishStep value) => output.WriteLine(value switch
        {
            PublishStep.Fork => $"Forking {registry}...",
            PublishStep.Commit => "Committing the pack...",
            _ => "Opening the pull request...",
        });
    }
}
