using System.CommandLine;
using System.Globalization;
using System.Reflection;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>pack rate &lt;game&gt;/&lt;language&gt;/&lt;pack&gt; [--like | --unlike | --works | --doesnt-work] [--text …]
/// [--revision N] [--build B] [--discussion URL]</c> (task 63): the pack's Discussion from the registry index (or
/// <c>--discussion</c>); without an action it prints the Discussion as the signed-in player sees it (likes, own like,
/// reports); <c>--like</c> / <c>--unlike</c> give or take back the 👍; <c>--works</c> / <c>--doesnt-work</c> post the
/// report of the revision (default: the index's) with the game's build (default: the catalog's), replacing the
/// player's earlier report of that revision. Signed in with <c>github login</c>. Exit 0 = done; 1 = not (reason on stderr).
/// </summary>
internal static class PackRateCommand
{
    public static Command Build(OsEnvironment env)
    {
        var packArgument = new Argument<string>("pack") { Description = "The registry pack: <game id>/<language>/<pack id>." };
        var like = new Option<bool>("--like") { Description = "Give the pack's Discussion your 👍." };
        var unlike = new Option<bool>("--unlike") { Description = "Take your 👍 back." };
        var works = new Option<bool>("--works") { Description = "Report that the pack works." };
        var doesNotWork = new Option<bool>("--doesnt-work") { Description = "Report that the pack doesn't work." };
        var text = new Option<string?>("--text") { Description = "Your comment, posted with the report." };
        var revisionOption = new Option<int?>("--revision") { Description = "The revision you played (default: the index's current one)." };
        var buildOption = new Option<string?>("--build") { Description = "The game build you played (default: the game catalog's)." };
        var discussionOption = new Option<string?>("--discussion") { Description = "The Discussion's URL, instead of the one the registry index lists." };
        var rate = new Command(
            "rate",
            "Like a registry pack or report whether it works, in its GitHub Discussion (sign in first with 'github login'). Without an action, shows the Discussion.")
        {
            packArgument, like, unlike, works, doesNotWork, text, revisionOption, buildOption, discussionOption,
        };
        rate.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var actions = new[] { result.GetValue(like), result.GetValue(unlike), result.GetValue(works), result.GetValue(doesNotWork) }.Count(a => a);
            if (actions > 1)
            {
                await error.WriteLineAsync("Give one of --like, --unlike, --works, --doesnt-work.");
                return 1;
            }

            var parts = result.GetValue(packArgument)!.Split('/');
            if (parts.Length != 3 || !GameId.TryParse(parts[0], out var game) || parts[1].Length == 0 || parts[2].Length == 0)
            {
                await error.WriteLineAsync($"'{result.GetValue(packArgument)}' is not <game id>/<language>/<pack id>.");
                return 1;
            }

            var (language, packId) = (parts[1], parts[2]);
            var paths = AppPaths.Resolve(env);
            var pack = await FindPackAsync(env, paths, game, language, packId, error, cancellationToken);
            Uri? link = pack?.Discussion;
            if (result.GetValue(discussionOption) is { } url && !Uri.TryCreate(url, UriKind.Absolute, out link))
            {
                link = null;
            }

            if (!DiscussionRef.TryParse(link, out var discussion))
            {
                await error.WriteLineAsync(link is null
                    ? $"The registry index lists no Discussion for {game}/{language}/{packId}; give --discussion <url>."
                    : $"'{link}' is not a GitHub Discussion URL.");
                return 1;
            }

            using var http = GitHubCommand.CreateHttpClient();
            var session = GitHubCommand.CreateSession(env, http);
            var token = await session.GetTokenAsync(cancellationToken);
            if (token is not { IsOk: true, Value: { Length: > 0 } value })
            {
                await error.WriteLineAsync(token.Status == SecretStatus.NotFound ? "Not signed in: run 'ailocalpack github login' first." : $"Can't read the GitHub sign-in: {token}");
                return 1;
            }

            var client = new PackDiscussionClient(http, session.App);
            if (result.GetValue(like) || result.GetValue(unlike))
            {
                var liked = await client.SetLikeAsync(discussion, result.GetValue(like), value, cancellationToken);
                if (!liked.IsOk)
                {
                    return await FailAsync(error, liked.Error);
                }

                await output.WriteLineAsync($"{(liked.Value.Liked ? "Liked" : "Not liked")}: {liked.Value.Likes} 👍 on {discussion}");
                return 0;
            }

            if (result.GetValue(works) || result.GetValue(doesNotWork))
            {
                var revision = result.GetValue(revisionOption) ?? pack?.Revision;
                if (revision is not > 0)
                {
                    await error.WriteLineAsync("The registry index doesn't list the pack: give --revision <n>.");
                    return 1;
                }

                var build = result.GetValue(buildOption) ?? PackReport.BuildOf(PackCommand.FindGame(env, paths, game)?.Version);
                var verdict = result.GetValue(works) ? PackVerdict.Works : PackVerdict.DoesNotWork;
                var app = $"{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"} {env.Os.ToString().ToLowerInvariant()} (CLI)";
                var sent = await client.ReportAsync(discussion, new PackReport(verdict, game, language, packId, revision.Value, build, result.GetValue(text), app), value, cancellationToken);
                if (!sent.IsOk)
                {
                    return await FailAsync(error, sent.Error);
                }

                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{(sent.Value.Replaced ? "Replaced your report" : "Reported")}: {(verdict == PackVerdict.Works ? "works" : "doesn't work")}, rev {revision}, build {build ?? "unknown"}: {sent.Value.Url?.AbsoluteUri ?? discussion.ToString()}"));
                return 0;
            }

            var read = await client.ReadAsync(discussion, value, cancellationToken);
            if (!read.IsOk)
            {
                return await FailAsync(error, read.Error);
            }

            var state = read.Value;
            await output.WriteLineAsync($"{discussion}: {state.Url?.AbsoluteUri}{(state.Locked ? " (locked)" : string.Empty)}");
            await output.WriteLineAsync($"  {state.Likes} 👍{(state.ViewerLiked ? $", one of them yours ({state.ViewerLogin})" : string.Empty)}");
            foreach (var report in state.Reports)
            {
                var r = report.Report;
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  {(r.Verdict == PackVerdict.Works ? "works" : "doesn't work")}: {r.Game}/{r.Language}/{r.PackId} rev {r.Revision}, build {r.Build ?? "unknown"}{(report.ByViewer ? " (yours)" : string.Empty)}"));
            }

            return 0;
        });
        return rate;
    }

    private static async Task<int> FailAsync(TextWriter error, RatingError failed)
    {
        await error.WriteLineAsync(failed.Problem switch
        {
            RatingProblem.Locked => "The Discussion is locked.",
            RatingProblem.NotFound => $"The Discussion isn't there: {failed.Message}",
            RatingProblem.Unauthorized => "GitHub no longer accepts the sign-in: run 'ailocalpack github login' again.",
            RatingProblem.RateLimited when failed.RetryAt is { } at => $"GitHub's rate limit is used up until {at.ToLocalTime():t}.",
            _ => $"{failed.Problem}: {failed.Message}",
        });
        return 1;
    }

    private static async Task<RegistryPack?> FindPackAsync(OsEnvironment env, AppPaths paths, GameId game, string language, string packId, TextWriter error, CancellationToken cancellationToken)
    {
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
        if (!RegistryLocation.TryResolve(env, settings.RegistryIndexUrl, out var location, out var locationError))
        {
            await error.WriteLineAsync($"warning: registry index not read: {locationError}");
            return null;
        }

        using var http = IndexCommand.CreateHttpClient();
        var snapshot = await new RegistryIndexClient(http, paths).FetchAsync(location, cancellationToken);
        if (snapshot.Index is null)
        {
            await error.WriteLineAsync($"warning: can't read the registry index at {location} ({snapshot.Detail}).");
            return null;
        }

        return snapshot.Index.PacksFor(game, language).FirstOrDefault(p => p.Id == packId);
    }
}
