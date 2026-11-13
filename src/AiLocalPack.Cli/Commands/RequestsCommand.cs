using System.CommandLine;
using System.Globalization;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Translation requests (task 74). <c>requests collect &lt;registry root&gt; --out &lt;file&gt; [--repo owner/repo]
/// [--close] [--github]</c>: the registry's Discussions of the <c>Requests</c> category → <c>requests.json</c> for
/// <c>index build --requests</c>, as the registry's Publish index workflow runs it (token from <c>GITHUB_TOKEN</c>, else
/// the <c>github login</c> sign-in). <c>request &lt;game&gt;/&lt;language&gt; [--ask [--name …] [--note …] | --withdraw]
/// [--discussion URL]</c>: the player's side (sign in with <c>github login</c>): ask or add a vote, take it back, or, without an
/// action, show the request. Exit 0 = done; 1 = not (reason on stderr).
/// </summary>
internal static class RequestsCommand
{
    public static Command BuildCollect(OsEnvironment env)
    {
        var root = new Argument<string>("registry-root") { Description = "The registry checkout (holds games/ and policy/)." };
        var outFile = new Option<string>("--out") { Description = "Where to write requests.json.", Required = true };
        var repoOption = new Option<string?>("--repo")
        {
            Description = $"The registry repository whose Discussions are read (default: {RegistryRepo.Default.FullName}, or {RegistryRepo.Variable}).",
        };
        var close = new Option<bool>("--close")
        {
            Description = "Close the Discussions of done requests (a pack exists now) and newer duplicates (the token needs discussions: write).",
        };
        var github = new Option<bool>("--github") { Description = "Print warnings as GitHub Actions annotations (::warning ...)." };
        var collect = new Command(
            "collect",
            $"Collect the open translation requests from the registry's \"{RequestQueries.Category}\" Discussions (👍 = votes, the newest live claim) into requests.json. Token: {RatingsCommand.TokenVariable}, else 'github login'.")
        {
            root, outFile, repoOption, close, github,
        };
        collect.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var rootPath = Path.GetFullPath(result.GetValue(root)!);
            var annotate = result.GetValue(github);
            if (!Directory.Exists(rootPath))
            {
                await error.WriteLineAsync($"The registry folder {rootPath} doesn't exist.");
                return 1;
            }

            var repo = RegistryRepo.Resolve(env);
            if (result.GetValue(repoOption) is { } repoText && !RegistryRepo.TryParse(repoText, out repo))
            {
                await error.WriteLineAsync($"'{repoText}' is not a repository (owner/name).");
                return 1;
            }

            using var http = GitHubCommand.CreateHttpClient();
            var token = await TokenAsync(env, http, error, cancellationToken);
            if (token is null)
            {
                return 1;
            }

            var collected = await new RequestCollector(new RegistryDiscussionClient(http, GitHubOAuthApp.Resolve(env), token))
                .CollectAsync(rootPath, repo, result.GetValue(close), cancellationToken);
            if (!collected.IsOk)
            {
                var failed = collected.Error;
                await error.WriteLineAsync(failed.Problem switch
                {
                    RatingProblem.Unauthorized => $"GitHub doesn't accept the token: {failed.Message}",
                    RatingProblem.RateLimited when failed.RetryAt is { } at => $"GitHub's rate limit is used up until {at.ToUniversalTime():u}.",
                    _ => $"Can't read the requests of {repo.FullName} ({failed.Problem}): {failed.Message}",
                });
                await error.WriteLineAsync("Nothing written.");
                return 1;
            }

            var collection = collected.Value;
            foreach (var warning in collection.Warnings)
            {
                await error.WriteLineAsync(annotate
                    ? GitHubAnnotations.Format("warning", warning.File.Length > 0 ? Path.Combine(rootPath, warning.File) : null, warning.Field, warning.Message)
                    : $"warning: {warning}");
            }

            foreach (var line in collection.Lines)
            {
                await output.WriteLineAsync(Line(line));
            }

            var outPath = result.GetValue(outFile)!;
            try
            {
                AtomicFile.WriteAllBytes(outPath, Encoding.UTF8.GetBytes(RegistryRequests.Write(collection.Requests)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Can't write {outPath}: {e.Message}");
                return 1;
            }

            var closed = collection.Lines.Sum(l => l.Closed);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Wrote {outPath}: {collection.Requests.Count} open request{(collection.Requests.Count == 1 ? "" : "s")}, {closed} Discussion{(closed == 1 ? "" : "s")} closed."));
            return 0;
        });

        return new Command("requests", "Translation requests of the registry (the registry's scheduled Action).") { collect };
    }

    public static Command BuildRequest(OsEnvironment env)
    {
        var keyArgument = new Argument<string>("request") { Description = "The request: <game id>/<language>, e.g. steam:620/ru." };
        var ask = new Option<bool>("--ask") { Description = "Ask for the translation (or add your vote to the open request)." };
        var withdraw = new Option<bool>("--withdraw") { Description = "Take your vote back." };
        var name = new Option<string?>("--name") { Description = "The game's name for a new request's title (default: the game catalog's)." };
        var note = new Option<string?>("--note") { Description = "A note for the translator, posted with your request." };
        var discussionOption = new Option<string?>("--discussion") { Description = "The request's Discussion URL, instead of the registry index's or a search." };
        var request = new Command(
            "request",
            "Ask for a translation of a game into a language, or take your vote back, in the registry's Requests Discussions (sign in first with 'github login'). Without an action, shows the request.")
        {
            keyArgument, ask, withdraw, name, note, discussionOption,
        };
        request.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (result.GetValue(ask) && result.GetValue(withdraw))
            {
                await error.WriteLineAsync("Give one of --ask, --withdraw.");
                return 1;
            }

            if (!RequestKey.TryParse(result.GetValue(keyArgument), out var key))
            {
                await error.WriteLineAsync($"'{result.GetValue(keyArgument)}' is not <game id>/<language> (a supported language tag).");
                return 1;
            }

            var paths = AppPaths.Resolve(env);
            var known = await FindRequestAsync(env, paths, key, error, cancellationToken);
            var link = known?.Discussion;
            if (result.GetValue(discussionOption) is { } url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out link) || !DiscussionRef.TryParse(link, out _))
                {
                    await error.WriteLineAsync($"'{url}' is not a GitHub Discussion URL.");
                    return 1;
                }
            }

            using var http = GitHubCommand.CreateHttpClient();
            var session = GitHubCommand.CreateSession(env, http);
            var token = await session.GetTokenAsync(cancellationToken);
            if (token is not { IsOk: true, Value: { Length: > 0 } value })
            {
                await error.WriteLineAsync(token.Status == SecretStatus.NotFound ? "Not signed in: run 'ailocalpack github login' first." : $"Can't read the GitHub sign-in: {token}");
                return 1;
            }

            var client = new TranslationRequestClient(http, session.App);
            var repo = RegistryRepo.Resolve(env);
            RatingResult<RequestState> state;
            if (result.GetValue(ask))
            {
                var gameName = result.GetValue(name) ?? PackCommand.FindGame(env, paths, key.Game)?.Name ?? key.Game.ToString();
                state = await client.AskAsync(repo, key, gameName, link, result.GetValue(note), value, cancellationToken);
                if (state.IsOk)
                {
                    new TranslationRequestStore(paths).Ask(key, state.Value.Url, DateTimeOffset.UtcNow);
                }
            }
            else
            {
                if (!DiscussionRef.TryParse(link, out var discussion))
                {
                    var found = await client.FindAsync(repo, key, value, cancellationToken);
                    if (!found.IsOk)
                    {
                        return await FailAsync(error, found.Error);
                    }

                    discussion = found.Value;
                }

                if (discussion is null)
                {
                    await output.WriteLineAsync($"Nobody asked for {key} yet.");
                    return 0;
                }

                state = result.GetValue(withdraw)
                    ? await client.WithdrawAsync(discussion, value, cancellationToken)
                    : await client.ReadAsync(discussion, value, cancellationToken);
                if (state.IsOk && result.GetValue(withdraw))
                {
                    new TranslationRequestStore(paths).Withdraw(key);
                }
            }

            if (!state.IsOk)
            {
                return await FailAsync(error, state.Error);
            }

            var s = state.Value;
            await output.WriteLineAsync($"{key}: {s.Url.AbsoluteUri}{(s.Closed ? " (closed)" : "")}{(s.Locked ? " (locked)" : "")}");
            await output.WriteLineAsync($"  {s.Votes} vote{(s.Votes == 1 ? "" : "s")}{(s.Voted ? $", one of them yours ({s.ViewerLogin})" : "")}");
            if (s.LiveClaim(DateTimeOffset.UtcNow) is { } claim)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  {claim.Comment.Author} {RequestClaimComment.StateText(claim.Claim.State)}: {(claim.Claim.Stage is { } stage ? RequestClaimComment.StageText(stage) + " " : "")}{claim.Claim.Percent}%, last news {claim.Comment.SaidAt.ToLocalTime():yyyy-MM-dd HH:mm}{(claim.Comment.ByViewer ? " (yours)" : "")}"));
            }

            foreach (var n in s.Notes.TakeLast(10))
            {
                await output.WriteLineAsync($"  {n.Author}: {n.Body.ReplaceLineEndings(" ")}");
            }

            return 0;
        });
        return request;
    }

    /// <summary><c>steam:620/fr: 13 votes, alice translating (translate 45%), 1 closed https://…</c>.</summary>
    private static string Line(CollectedRequest line)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{line.Key}: {line.Votes} vote{(line.Votes == 1 ? "" : "s")}"));
        text.Append(line.Outcome switch
        {
            RequestOutcome.Done => ", done (a pack exists)",
            RequestOutcome.OptedOut => ", left out (the game opted out)",
            RequestOutcome.NoVotes => ", left out (no votes)",
            _ => "",
        });
        if (line.Claim is { } claim)
        {
            text.Append(", ").Append(claim.By).Append(' ').Append(RequestClaimComment.StateText(claim.State));
            if (claim.Stage is { } stage)
            {
                text.Append(string.Create(CultureInfo.InvariantCulture, $" ({RequestClaimComment.StageText(stage)} {claim.Percent}%)"));
            }
        }

        if (line.Closed > 0)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $", {line.Closed} closed"));
        }

        return text.Append(' ').Append(string.Join(" ", line.Discussions.Select(d => d.AbsoluteUri))).ToString();
    }

    private static async Task<string?> TokenAsync(OsEnvironment env, HttpClient http, TextWriter error, CancellationToken cancellationToken)
    {
        if (env.GetVariable(RatingsCommand.TokenVariable) is { Length: > 0 } fromEnv)
        {
            return fromEnv.Trim();
        }

        var signedIn = await GitHubCommand.CreateSession(env, http).GetTokenAsync(cancellationToken);
        if (signedIn is { IsOk: true, Value: { Length: > 0 } value })
        {
            return value;
        }

        await error.WriteLineAsync(signedIn.Status == SecretStatus.NotFound
            ? $"No GitHub token: set {RatingsCommand.TokenVariable} or run 'ailocalpack github login' first."
            : $"Can't read the GitHub sign-in: {signedIn}");
        return null;
    }

    private static async Task<int> FailAsync(TextWriter error, RatingError failed)
    {
        await error.WriteLineAsync(failed.Problem switch
        {
            RatingProblem.Locked => "The Discussion is locked.",
            RatingProblem.NotFound => $"Not found: {failed.Message}",
            RatingProblem.Unauthorized => "GitHub no longer accepts the sign-in: run 'ailocalpack github login' again.",
            RatingProblem.RateLimited when failed.RetryAt is { } at => $"GitHub's rate limit is used up until {at.ToLocalTime():t}.",
            _ => $"{failed.Problem}: {failed.Message}",
        });
        return 1;
    }

    private static async Task<RegistryRequest?> FindRequestAsync(OsEnvironment env, AppPaths paths, RequestKey key, TextWriter error, CancellationToken cancellationToken)
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

        return snapshot.Index.RequestFor(key.Game, key.Language);
    }
}
