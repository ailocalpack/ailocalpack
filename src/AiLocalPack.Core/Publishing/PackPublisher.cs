using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Publishing;

/// <summary>The three steps the player sees while a pack is sent (design §7: Fork the registry → Commit the pack → Open a pull request).</summary>
public enum PublishStep
{
    Fork,
    Commit,
    PullRequest,
}

/// <summary>What to publish and where.</summary>
/// <param name="Index">The registry index, for the publisher's opt-out flag (none: not checked here; registry CI checks it).</param>
/// <param name="GameName">The game's name for the PR title and body (the game ID when absent).</param>
public sealed record PublishRequest(LocalPack Pack, PublishTarget Target, RegistryRepo Registry, RegistryIndex? Index = null, string? GameName = null);

/// <summary>How <see cref="PackPublisher.PublishAsync"/> ended.</summary>
public abstract record PublishResult
{
    private PublishResult()
    {
    }

    /// <param name="Manifest">The manifest as committed (registry ID, revision, the player as author).</param>
    /// <param name="Fork">The player's fork, <c>login/repo</c>.</param>
    /// <param name="Resubmitted">The PR was already open: the pack was pushed to its branch (a fix), no new PR.</param>
    /// <param name="Unchanged">The branch already held exactly these files: nothing was committed.</param>
    public sealed record Published(int PullRequest, Uri PullRequestUrl, PackManifest Manifest, string Fork, string Branch, bool Resubmitted, bool Unchanged) : PublishResult;

    /// <param name="Step">The step it stopped at (<c>null</c>: before the first, e.g. not signed in).</param>
    /// <param name="Message">One plain sentence or two for the player.</param>
    /// <param name="RetryAt">For <see cref="PublishProblem.RateLimited"/>: when GitHub accepts requests again, if it said.</param>
    /// <param name="Problems">For <see cref="PublishProblem.Invalid"/>: what the registry's checks refuse.</param>
    public sealed record Failed(PublishStep? Step, PublishProblem Problem, string Message, DateTimeOffset? RetryAt = null, IReadOnlyList<PackProblem>? Problems = null) : PublishResult;
}

/// <summary>
/// Submits a local pack to the registry as a pull request from the player's fork (task 60), with the player's GitHub
/// token from <see cref="GitHubSession"/>. <b>Fork</b>: <c>POST /repos/&lt;registry&gt;/forks</c> (GitHub answers with
/// the existing fork when there is one, whatever its name), waits until its default branch is readable, then
/// <c>merge-upstream</c> (best effort); the base is the registry's default branch head. <b>Commit</b>: the target is
/// resolved against the registry at that commit (a new pack's ID made free among the folder's packs, a revision's
/// existing manifest read from it), the manifest built (<see cref="PublishPayloads"/>), the folder staged and checked
/// with <see cref="PackValidator"/> as CI would; a patch pack's archive goes to the fork's <c>patches</c> Release as
/// an asset (replaced when its digest differs); then Git Data API: blobs → tree (on the parent's tree, the pack
/// folder's old files not in the pack deleted) → commit → ref. Branch <see cref="PublishPayload.Branch"/>: with an
/// open PR from it the commit goes on top (a fix: same PR); without one (an earlier try, a closed PR) it is reset to
/// the new commit on the registry's head. <b>Pull request</b>: opened against the default branch with
/// <c>maintainer_can_modify</c>, or the open one reused. The local pack's report records the PR. Never throws except
/// for cancellation; every problem is a <see cref="PublishResult.Failed"/> with a plain message. Running it again
/// after a failure continues where it stopped (every step is idempotent).
/// </summary>
public sealed class PackPublisher
{
    private const string PatchMediaType = "application/zip";

    private readonly GitHubSession _session;
    private readonly HttpClient _http;
    private readonly LocalPackStore _localPacks;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    public PackPublisher(
        GitHubSession session,
        HttpClient http,
        LocalPackStore localPacks,
        AppPaths paths,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<PackPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(localPacks);
        ArgumentNullException.ThrowIfNull(paths);
        _session = session;
        _http = http;
        _localPacks = localPacks;
        _paths = paths;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, ct) => Task.Delay(span, _time, ct));
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>How long to wait for a new fork to become readable.</summary>
    public static TimeSpan ForkWait { get; } = TimeSpan.FromMinutes(3);

    public async Task<PublishResult> PublishAsync(PublishRequest request, IProgress<PublishStep>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = request.Pack.Manifest;
        if (request.Index?.Find(local.Game) is { OptOut: true })
        {
            return new PublishResult.Failed(null, PublishProblem.OptedOut, $"The publisher of {request.GameName ?? local.Game.ToString()} asked not to distribute fan translations, so the registry doesn't take packs for it.");
        }

        var token = await _session.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        switch (token.Status)
        {
            case SecretStatus.NotFound:
                return new PublishResult.Failed(null, PublishProblem.NotSignedIn, "Sign in to GitHub first: packs are shared as pull requests from your GitHub account.");
            case not SecretStatus.Ok:
                return new PublishResult.Failed(null, PublishProblem.Keychain, $"Can't read your GitHub sign-in from the keychain: {token.Problem}");
        }

        var api = new GitHubRestClient(_http, _session.App, token.Value!, _time);
        PublishStep? step = null;
        try
        {
            var player = await PlayerAsync(token.Value!, cancellationToken).ConfigureAwait(false);

            step = PublishStep.Fork;
            progress?.Report(PublishStep.Fork);
            var registry = request.Registry;
            var upstream = await api.GetAsync($"repos/{registry.FullName}", cancellationToken).ConfigureAwait(false);
            var defaultBranch = Text(upstream, "default_branch") ?? "master";
            var fork = await ForkAsync(api, registry, defaultBranch, cancellationToken).ConfigureAwait(false);
            var head = await api.GetAsync($"repos/{registry.FullName}/git/ref/heads/{defaultBranch}", cancellationToken).ConfigureAwait(false);
            var baseSha = Text(Property(head, "object"), "sha") ?? throw Unexpected("GitHub's answer for the registry's branch has no commit.");

            step = PublishStep.Commit;
            progress?.Report(PublishStep.Commit);
            var (plan, refused) = await PayloadAsync(api, request, player, baseSha, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                return refused! with { Step = PublishStep.Commit };
            }

            var problems = Validate(plan, cancellationToken);
            if (problems.Count > 0)
            {
                return new PublishResult.Failed(PublishStep.Commit, PublishProblem.Invalid, $"The registry would refuse this pack: {problems[0]}", Problems: problems);
            }

            Uri? forkAsset = null;
            if (plan.PatchArchiveName is { } archiveName)
            {
                forkAsset = await UploadPatchAsync(api, fork, defaultBranch, request.Pack, plan, archiveName, cancellationToken).ConfigureAwait(false);
            }

            var openPr = await OpenPullRequestAsync(api, registry, fork.Owner, plan.Branch, cancellationToken).ConfigureAwait(false);
            var (committed, unchanged) = await CommitAsync(api, fork, plan, baseSha, openPr is not null, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Committed {Folder} rev {Revision} to {Fork}:{Branch} ({Commit}{Unchanged})", plan.Folder, plan.Manifest.Revision, fork.FullName, plan.Branch, committed, unchanged ? ", unchanged" : "");

            step = PublishStep.PullRequest;
            progress?.Report(PublishStep.PullRequest);
            var pr = openPr ?? await CreatePullRequestAsync(api, registry, fork.Owner, defaultBranch, plan, request, forkAsset, cancellationToken).ConfigureAwait(false);
            var published = new PublishResult.Published(pr.Number, pr.Url, plan.Manifest, fork.FullName, plan.Branch, openPr is not null, unchanged);
            Record(request, published);
            _logger.LogInformation("Published {Folder} rev {Revision} as {Registry}#{Number} ({Kind})", plan.Folder, plan.Manifest.Revision, registry.FullName, pr.Number, openPr is null ? "new PR" : "pushed to the open PR");
            return published;
        }
        catch (GitHubApiException e)
        {
            _logger.LogWarning("Publishing {Game} {Language} {Id} stopped at {Step}: {Problem} {Status} {Message}", local.Game, local.Language, local.Id, step, e.Error.Problem, e.Error.Status, e.Error.Message);
            return new PublishResult.Failed(step, e.Error.Problem, Explain(e.Error, step, request.Registry), e.Error.RetryAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Publishing {Game} {Language} {Id}: a local file can't be read", local.Game, local.Language, local.Id);
            return new PublishResult.Failed(step, PublishProblem.Unexpected, $"A file of the pack can't be read: {e.Message}");
        }
    }

    /// <summary>The plain message for a failed call.</summary>
    public static string Explain(GitHubApiError error, PublishStep? step, RegistryRepo registry)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(registry);
        var what = step switch
        {
            PublishStep.Fork => "forking the registry",
            PublishStep.Commit => "committing the pack",
            PublishStep.PullRequest => "opening the pull request",
            _ => "checking your account",
        };
        return error.Problem switch
        {
            PublishProblem.RateLimited => error.RetryAt is { } at
                ? $"GitHub's request limit for your account is used up until {at.ToUniversalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} UTC; publish again after that (nothing is lost)."
                : "GitHub's request limit for your account is used up for now; publish again in a few minutes (nothing is lost).",
            PublishProblem.Unauthorized => "GitHub no longer accepts your sign-in (access revoked or expired); sign in to GitHub again.",
            PublishProblem.Forbidden => $"GitHub refused {what}: {error.Message} Signing out and in again gives the app the access it asks for (public_repo).",
            PublishProblem.NotFound when step == PublishStep.Fork => $"The registry {registry} isn't on GitHub (or can't be seen with your account).",
            PublishProblem.NotFound => $"GitHub couldn't find something while {what}: {error.Message}",
            PublishProblem.Rejected => $"GitHub refused {what}: {error.Message}",
            PublishProblem.Conflict => $"GitHub couldn't finish {what} ({error.Message}); publish again in a moment.",
            PublishProblem.Network => $"GitHub couldn't be reached while {what} ({error.Message}); check the connection and publish again: it continues where it stopped.",
            PublishProblem.ForkNotReady => error.Message,
            _ => $"Unexpected answer from GitHub while {what}: {error.Message}",
        };
    }

    private async Task<PackAuthor> PlayerAsync(string token, CancellationToken cancellationToken)
    {
        switch (await new GitHubUserClient(_http, _session.App).GetUserAsync(token, cancellationToken).ConfigureAwait(false))
        {
            case GitHubUserResult.Found found:
                var name = found.User.Name is { } n && !string.Equals(n, found.User.Login, StringComparison.Ordinal) && PackFormat.IsText(n, 100) ? n : null;
                return new PackAuthor(found.User.Login, name);
            case GitHubUserResult.Unauthorized:
                throw new GitHubApiException(new GitHubApiError(PublishProblem.Unauthorized, 401, "Bad credentials."));
            case GitHubUserResult.Failed failed:
                throw new GitHubApiException(new GitHubApiError(failed.IsNetwork ? PublishProblem.Network : PublishProblem.Unexpected, 0, failed.Detail));
            default:
                throw Unexpected("Unknown answer for the account.");
        }
    }

    private async Task<Repo> ForkAsync(GitHubRestClient api, RegistryRepo registry, string defaultBranch, CancellationToken cancellationToken)
    {
        var created = await api.PostAsync($"repos/{registry.FullName}/forks", new JsonObject { ["default_branch_only"] = true }, cancellationToken).ConfigureAwait(false);
        var fullName = Text(created, "full_name") ?? throw Unexpected("GitHub's fork answer has no repository name.");
        var owner = Text(Property(created, "owner"), "login") ?? fullName.Split('/')[0];
        var fork = new Repo(owner, fullName);

        // A new fork is made in the background: wait until its branch can be read.
        var deadline = _time.GetUtcNow() + ForkWait;
        var wait = TimeSpan.FromSeconds(1);
        while (await api.TryGetAsync($"repos/{fullName}/git/ref/heads/{defaultBranch}", cancellationToken, emptyIsMissing: true).ConfigureAwait(false) is null)
        {
            if (_time.GetUtcNow() >= deadline)
            {
                throw new GitHubApiException(new GitHubApiError(PublishProblem.ForkNotReady, 0, $"GitHub is still making your fork {fullName}; publish again in a few minutes."));
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
            wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 10));
        }

        try
        {
            await api.PostAsync($"repos/{fullName}/merge-upstream", new JsonObject { ["branch"] = defaultBranch }, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException e) when (e.Error.Problem is PublishProblem.Conflict or PublishProblem.Rejected or PublishProblem.NotFound)
        {
            // The pack branch starts from the registry's own head, so an out-of-date fork branch doesn't matter.
            _logger.LogInformation("Fork {Fork} not synced with {Registry}: {Message}", fullName, registry, e.Error.Message);
        }

        return fork;
    }

    private async Task<(PublishPayload? Plan, PublishResult.Failed? Refused)> PayloadAsync(GitHubRestClient api, PublishRequest request, PackAuthor player, string baseSha, CancellationToken cancellationToken)
    {
        var local = request.Pack.Manifest;
        var languageFolder = $"{RegistryLayout.GamesFolder}/{RegistryLayout.GameFolder(local.Game)}/{local.Language}";
        var listing = await api.TryGetAsync($"repos/{request.Registry.FullName}/contents/{languageFolder}?ref={baseSha}", cancellationToken).ConfigureAwait(false);
        var taken = listing is { ValueKind: JsonValueKind.Array } entries
            ? entries.EnumerateArray().Where(e => Text(e, "type") == "dir").Select(e => Text(e, "name")).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var now = _time.GetUtcNow();
        PackManifest manifest;
        PackManifest? existing = null;
        switch (request.Target)
        {
            case PublishTarget.RevisionOf revision:
                if (!taken.Contains(revision.PackId)
                    || await api.TryGetRawAsync($"repos/{request.Registry.FullName}/contents/{languageFolder}/{revision.PackId}/{RegistryLayout.ManifestFileName}?ref={baseSha}", cancellationToken).ConfigureAwait(false) is not { } bytes)
                {
                    return (null, new PublishResult.Failed(null, PublishProblem.TargetGone, $"The registry has no pack «{revision.PackId}» for {local.Game} in {local.Language} any more; publish it as a separate pack."));
                }

                var read = PackManifestSerializer.Read(Encoding.UTF8.GetString(bytes));
                if (read.Value is not { } value)
                {
                    return (null, new PublishResult.Failed(null, PublishProblem.Unexpected, $"The registry's manifest of «{revision.PackId}» can't be read ({read.Errors.FirstOrDefault()}); publish it as a separate pack."));
                }

                existing = value;
                manifest = PublishPayloads.Revision(local, value, player, now, request.Registry);
                break;
            default:
                manifest = PublishPayloads.NewPack(local, player, PublishPayloads.FreeId(local.Id, taken), now, request.Registry);
                break;
        }

        var files = new List<PublishFile> { new(RegistryLayout.ManifestFileName, Encoding.UTF8.GetBytes(PackManifestSerializer.Write(manifest))) };
        if (local.Strings is { } strings)
        {
            files.Add(new PublishFile(strings.Path, await File.ReadAllBytesAsync(Path.Combine(request.Pack.Folder, strings.Path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false)));
        }

        var archiveName = manifest.Patch is null ? null : LocalPackStore.PatchArchiveName(manifest.Game, manifest.Language, manifest.Id, manifest.Revision);
        return (new PublishPayload(manifest, existing, files, archiveName), null);
    }

    /// <summary>The registry's checks on the folder as it will be committed (staged in the cache, laid out like the registry).</summary>
    private List<PackProblem> Validate(PublishPayload plan, CancellationToken cancellationToken)
    {
        var root = Path.Combine(_paths.Cache, "publish", Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, plan.Folder.Replace('/', Path.DirectorySeparatorChar));
            foreach (var file in plan.Files)
            {
                var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Content);
            }

            return [.. PackValidator.Validate(folder, cancellationToken).Problems];
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(e, "Staging folder {Folder} not deleted", root);
            }
        }
    }

    private async Task<Uri> UploadPatchAsync(GitHubRestClient api, Repo fork, string defaultBranch, LocalPack pack, PublishPayload plan, string archiveName, CancellationToken cancellationToken)
    {
        var source = _localPacks.PatchArchivePath(pack.Manifest) ?? throw Unexpected("The local pack's patch URL names no archive.");
        var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (sha != plan.Manifest.Patch!.Sha256 || bytes.LongLength != plan.Manifest.Patch.Size)
        {
            throw new GitHubApiException(new GitHubApiError(PublishProblem.Unexpected, 0, $"The local patch archive {Path.GetFileName(source)} doesn't match its manifest (changed since it was built); make the pack again."));
        }

        var release = await api.TryGetAsync($"repos/{fork.FullName}/releases/tags/{RegistryRepo.PatchReleaseTag}", cancellationToken).ConfigureAwait(false)
            ?? await api.PostAsync($"repos/{fork.FullName}/releases", new JsonObject
            {
                ["tag_name"] = RegistryRepo.PatchReleaseTag,
                ["target_commitish"] = defaultBranch,
                ["name"] = "Patch archives",
                ["body"] = "Binary patch archives of packs submitted to the registry with AI Local Pack; registry CI copies each one to the registry when its pull request is merged.",
                ["prerelease"] = true,
            }, cancellationToken).ConfigureAwait(false);
        var releaseId = Long(release, "id") ?? throw Unexpected("GitHub's release answer has no ID.");
        for (var page = 1; ; page++)
        {
            var assets = await api.GetAsync($"repos/{fork.FullName}/releases/{releaseId.ToString(CultureInfo.InvariantCulture)}/assets?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            if (assets.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var asset in assets.EnumerateArray().Where(a => Text(a, "name") == archiveName))
            {
                if (Text(asset, "digest") == "sha256:" + sha && Url(asset, "browser_download_url") is { } url)
                {
                    return url;
                }

                await api.DeleteAsync($"repos/{fork.FullName}/releases/assets/{Long(asset, "id")?.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            }

            if (assets.GetArrayLength() < 100)
            {
                break;
            }
        }

        var template = Text(release, "upload_url") ?? throw Unexpected("GitHub's release answer has no upload address.");
        var brace = template.IndexOf('{', StringComparison.Ordinal);
        var upload = new Uri((brace >= 0 ? template[..brace] : template) + "?name=" + Uri.EscapeDataString(archiveName));
        var uploaded = await api.UploadAsync(upload, bytes, PatchMediaType, cancellationToken).ConfigureAwait(false);
        return Url(uploaded, "browser_download_url") ?? new Uri($"https://github.com/{fork.FullName}/releases/download/{RegistryRepo.PatchReleaseTag}/{Uri.EscapeDataString(archiveName)}");
    }

    private static async Task<PullRequest?> OpenPullRequestAsync(GitHubRestClient api, RegistryRepo registry, string forkOwner, string branch, CancellationToken cancellationToken)
    {
        var list = await api.GetAsync($"repos/{registry.FullName}/pulls?state=open&head={Uri.EscapeDataString($"{forkOwner}:{branch}")}", cancellationToken).ConfigureAwait(false);
        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(PullRequestOf).FirstOrDefault(p => p is not null) : null;
    }

    private static async Task<(string Commit, bool Unchanged)> CommitAsync(GitHubRestClient api, Repo fork, PublishPayload plan, string baseSha, bool prOpen, CancellationToken cancellationToken)
    {
        var branchRef = await api.TryGetAsync($"repos/{fork.FullName}/git/ref/heads/{plan.Branch}", cancellationToken).ConfigureAwait(false);
        var branchSha = branchRef is { } found ? Text(Property(found, "object"), "sha") : null;

        // A fix of an open PR goes on top of its branch; otherwise the branch starts again from the registry's head.
        var parent = prOpen && branchSha is not null ? branchSha : baseSha;
        var parentCommit = await api.GetAsync($"repos/{fork.FullName}/git/commits/{parent}", cancellationToken).ConfigureAwait(false);
        var parentTree = Text(Property(parentCommit, "tree"), "sha") ?? throw Unexpected("GitHub's commit answer has no tree.");

        var entries = new JsonArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in plan.Files)
        {
            var blob = await api.PostAsync($"repos/{fork.FullName}/git/blobs", new JsonObject
            {
                ["content"] = Convert.ToBase64String(file.Content),
                ["encoding"] = "base64",
            }, cancellationToken).ConfigureAwait(false);
            var path = $"{plan.Folder}/{file.Path}";
            paths.Add(path);
            entries.Add(new JsonObject { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = Text(blob, "sha") ?? throw Unexpected("GitHub's blob answer has no SHA.") });
        }

        foreach (var old in await FilesUnderAsync(api, fork, plan.Folder, parent, cancellationToken).ConfigureAwait(false))
        {
            if (!paths.Contains(old))
            {
                entries.Add(new JsonObject { ["path"] = old, ["mode"] = "100644", ["type"] = "blob", ["sha"] = null });
            }
        }

        var tree = await api.PostAsync($"repos/{fork.FullName}/git/trees", new JsonObject { ["base_tree"] = parentTree, ["tree"] = entries }, cancellationToken).ConfigureAwait(false);
        var treeSha = Text(tree, "sha") ?? throw Unexpected("GitHub's tree answer has no SHA.");
        if (treeSha == parentTree && branchSha == parent)
        {
            return (parent, true);
        }

        var commit = await api.PostAsync($"repos/{fork.FullName}/git/commits", new JsonObject
        {
            ["message"] = PublishPayloads.CommitMessage(plan),
            ["tree"] = treeSha,
            ["parents"] = new JsonArray(parent),
        }, cancellationToken).ConfigureAwait(false);
        var commitSha = Text(commit, "sha") ?? throw Unexpected("GitHub's commit answer has no SHA.");
        if (branchSha is null)
        {
            try
            {
                await api.PostAsync($"repos/{fork.FullName}/git/refs", new JsonObject { ["ref"] = $"refs/heads/{plan.Branch}", ["sha"] = commitSha }, cancellationToken).ConfigureAwait(false);
                return (commitSha, false);
            }
            catch (GitHubApiException e) when (e.Error.Problem == PublishProblem.Rejected)
            {
                // Made in the meantime ("Reference already exists"): move it below.
            }
        }

        await api.PatchAsync($"repos/{fork.FullName}/git/refs/heads/{plan.Branch}", new JsonObject { ["sha"] = commitSha, ["force"] = !prOpen }, cancellationToken).ConfigureAwait(false);
        return (commitSha, false);
    }

    /// <summary>Files under the pack folder at <paramref name="commit"/> (contents API, folder by folder), none when it doesn't exist.</summary>
    private static async Task<List<string>> FilesUnderAsync(GitHubRestClient api, Repo fork, string folder, string commit, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Queue<string>([folder]);
        while (pending.TryDequeue(out var next) && files.Count < 1000)
        {
            if (await api.TryGetAsync($"repos/{fork.FullName}/contents/{next}?ref={commit}", cancellationToken).ConfigureAwait(false) is not { ValueKind: JsonValueKind.Array } listing)
            {
                continue;
            }

            foreach (var entry in listing.EnumerateArray())
            {
                if (Text(entry, "path") is not { } path)
                {
                    continue;
                }

                if (Text(entry, "type") == "dir")
                {
                    pending.Enqueue(path);
                }
                else
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    private static async Task<PullRequest> CreatePullRequestAsync(
        GitHubRestClient api,
        RegistryRepo registry,
        string forkOwner,
        string defaultBranch,
        PublishPayload plan,
        PublishRequest request,
        Uri? forkAsset,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await api.PostAsync($"repos/{registry.FullName}/pulls", new JsonObject
            {
                ["title"] = PublishPayloads.PullRequestTitle(plan, request.GameName),
                ["head"] = $"{forkOwner}:{plan.Branch}",
                ["base"] = defaultBranch,
                ["body"] = PublishPayloads.PullRequestBody(plan, request.Pack.Report, request.GameName, forkAsset),
                ["maintainer_can_modify"] = true,
            }, cancellationToken).ConfigureAwait(false);
            return PullRequestOf(created) ?? throw Unexpected("GitHub's pull request answer has no number.");
        }
        catch (GitHubApiException e) when (e.Error.Problem == PublishProblem.Rejected && e.Error.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            if (await OpenPullRequestAsync(api, registry, forkOwner, plan.Branch, cancellationToken).ConfigureAwait(false) is { } open)
            {
                return open;
            }

            throw;
        }
    }

    private void Record(PublishRequest request, PublishResult.Published published)
    {
        var local = request.Pack.Manifest;
        try
        {
            _localPacks.UpdateReport(local.Game, local.Language, local.Id, report => report with
            {
                Published = new LocalPackPublication(
                    request.Registry.FullName,
                    published.Manifest.Id,
                    published.Manifest.Revision,
                    published.PullRequest,
                    published.PullRequestUrl,
                    published.Fork,
                    published.Branch,
                    _time.GetUtcNow()),
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "The PR of {Game} {Language} {Id} isn't recorded in its report", local.Game, local.Language, local.Id);
        }
    }

    private static PullRequest? PullRequestOf(JsonElement json) =>
        Long(json, "number") is { } number && Url(json, "html_url") is { } url ? new PullRequest((int)number, url) : null;

    private static GitHubApiException Unexpected(string message) => new(new GitHubApiError(PublishProblem.Unexpected, 0, message));

    private static JsonElement Property(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement json, string name) =>
        Property(json, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? Long(JsonElement json, string name) =>
        Property(json, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static Uri? Url(JsonElement json, string name) =>
        Uri.TryCreate(Text(json, name), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.IsLoopback) ? url : null;

    private sealed record Repo(string Owner, string FullName);

    private sealed record PullRequest(int Number, Uri Url);
}
