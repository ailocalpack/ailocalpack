using System.CommandLine;
using System.Globalization;
using System.Text;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>ratings collect &lt;registry root&gt; --out &lt;file&gt; [--repo owner/repo] [--create-missing] [--github]</c>
/// (task 64): the registry's pack Discussions → <c>ratings.json</c> for <c>index build --ratings</c>, as the registry's
/// Publish index workflow runs it. The token comes from <c>GITHUB_TOKEN</c> (the Action's), else the
/// <c>github login</c> sign-in. Exit 0 = written; 1 = nothing written (reason on stderr).
/// </summary>
internal static class RatingsCommand
{
    public const string TokenVariable = "GITHUB_TOKEN";

    public static Command Build(OsEnvironment env)
    {
        var root = new Argument<string>("registry-root") { Description = "The registry checkout (holds games/)." };
        var outFile = new Option<string>("--out") { Description = "Where to write ratings.json.", Required = true };
        var repoOption = new Option<string?>("--repo")
        {
            Description = $"The registry repository whose Discussions are read (default: {RegistryRepo.Default.FullName}, or {RegistryRepo.Variable}).",
        };
        var createMissing = new Option<bool>("--create-missing")
        {
            Description = $"Open a Discussion in the \"{RatingCollector.Category}\" category for every pack without one (the token needs discussions: write).",
        };
        var github = new Option<bool>("--github") { Description = "Print warnings as GitHub Actions annotations (::warning ...)." };
        var collect = new Command(
            "collect",
            $"Count every pack's 👍 and works / doesn't-work reports in the registry's Discussions (one per account, not the authors, accounts at least {RegistryVerification.MinAccountAge.Days} days old, the current revision and a supported build) into ratings.json. Token: {TokenVariable}, else 'github login'.")
        {
            root, outFile, repoOption, createMissing, github,
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
            var app = GitHubOAuthApp.Resolve(env);
            var token = env.GetVariable(TokenVariable) is { Length: > 0 } fromEnv ? fromEnv.Trim() : null;
            if (token is null)
            {
                var signedIn = await GitHubCommand.CreateSession(env, http).GetTokenAsync(cancellationToken);
                if (signedIn is not { IsOk: true, Value: { Length: > 0 } value })
                {
                    await error.WriteLineAsync(signedIn.Status == SecretStatus.NotFound
                        ? $"No GitHub token: set {TokenVariable} or run 'ailocalpack github login' first."
                        : $"Can't read the GitHub sign-in: {signedIn}");
                    return 1;
                }

                token = value;
            }

            var collected = await new RatingCollector(new RegistryDiscussionClient(http, app, token))
                .CollectAsync(rootPath, repo, result.GetValue(createMissing), cancellationToken);
            if (!collected.IsOk)
            {
                var failed = collected.Error;
                await error.WriteLineAsync(failed.Problem switch
                {
                    RatingProblem.Unauthorized => $"GitHub doesn't accept the token: {failed.Message}",
                    RatingProblem.RateLimited when failed.RetryAt is { } at => $"GitHub's rate limit is used up until {at.ToUniversalTime():u}.",
                    _ => $"Can't read the Discussions of {repo.FullName} ({failed.Problem}): {failed.Message}",
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

            foreach (var pack in collection.Packs)
            {
                await output.WriteLineAsync(Line(pack));
            }

            var outPath = result.GetValue(outFile)!;
            try
            {
                AtomicFile.WriteAllBytes(outPath, Encoding.UTF8.GetBytes(RegistryRatings.Write(collection.Ratings)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Can't write {outPath}: {e.Message}");
                return 1;
            }

            var opened = collection.Packs.Count(p => p.Created);
            var warnings = collection.Warnings.Count > 0
                ? string.Create(CultureInfo.InvariantCulture, $", {collection.Warnings.Count} warning{(collection.Warnings.Count == 1 ? "" : "s")}.")
                : ".";
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Wrote {outPath}: {collection.Packs.Count} pack{(collection.Packs.Count == 1 ? "" : "s")}, {opened} Discussion{(opened == 1 ? "" : "s")} opened{warnings}"));
            return 0;
        });

        return new Command("ratings", "Ratings of registry packs (the registry's scheduled Action).") { collect };
    }

    /// <summary><c>steam:620/ru/full rev 2: 3 works, 0 doesn't work, 2 👍, confirmed (left out: 1 author) https://…</c>.</summary>
    private static string Line(CollectedPack pack)
    {
        var rating = pack.Tally.Rating;
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"{pack.Pack.Key} rev {pack.Pack.Revision}: {rating.Works} works, {rating.DoesNotWork} doesn't work, {rating.Likes} 👍"));
        if (RegistryVerification.IsConfirmed(rating))
        {
            text.Append(", confirmed");
        }

        if (pack.Tally.Ignored.Count > 0)
        {
            text.Append(" (left out: ")
                .Append(string.Join(", ", pack.Tally.Ignored.OrderBy(i => i.Key).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Value} {Reason(i.Key)}"))))
                .Append(')');
        }

        text.Append(' ').Append(pack.Discussion is { } url ? url.AbsoluteUri : "no Discussion");
        if (pack.Created)
        {
            text.Append(" (opened now)");
        }

        return text.ToString();
    }

    private static string Reason(IgnoredReport reason) => reason switch
    {
        IgnoredReport.OtherPack => "about another pack",
        IgnoredReport.OtherRevision => "of another revision",
        IgnoredReport.Replaced => "replaced by a later one",
        IgnoredReport.NoAccount => "deleted account",
        IgnoredReport.Bot => "bot",
        IgnoredReport.Author => "author",
        IgnoredReport.YoungAccount => "young account",
        IgnoredReport.OtherBuild => "unsupported build",
        _ => reason.ToString(),
    };
}
