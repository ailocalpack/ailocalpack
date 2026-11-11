using System.Net;
using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Secrets;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>Fork → commit → PR against a fake GitHub (task 60).</summary>
public sealed class PackPublisherTests : IDisposable
{
    private const string Fork = "octo/registry";
    private const string Branch = "pack/steam-620/ru/claude-code/rev-1";
    private const string Folder = "games/steam-620/ru/claude-code";

    private readonly PublishBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public async Task Publishing_opens_a_pull_request_from_the_players_fork()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.PullRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Number = 1,
            Head = $"octo:{Branch}",
            Base = "master",
            MaintainerCanModify = true,
        }, o => o.ExcludingMissingMembers());
        result.Should().BeOfType<PublishResult.Published>().Which.PullRequestUrl.Should().Be(new Uri("https://github.com/ailocalpack/registry/pull/1"));
    }

    [Fact]
    public async Task The_branch_holds_the_pack_folder_on_top_of_the_registry()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.Files(Fork, Branch)!.Keys.Should().BeEquivalentTo("README.md", $"{Folder}/manifest.json", $"{Folder}/strings/strings.json.gz");
    }

    [Fact]
    public async Task The_string_table_is_committed_byte_for_byte()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.Files(Fork, Branch)![$"{Folder}/strings/strings.json.gz"].Should().Equal(File.ReadAllBytes(Path.Combine(pack.Folder, "strings", "strings.json.gz")));
    }

    [Fact]
    public async Task The_committed_manifest_names_the_player_instead_of_the_placeholder()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        var manifest = _bed.CommittedManifest(Fork, Branch, Folder);
        manifest.Authors.Should().Equal(new PackAuthor("octo", "Octo Cat"));
        manifest.Revisions.Should().ContainSingle().Which.By.Should().Be("octo");
    }

    [Fact]
    public async Task The_steps_are_reported_in_order()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        var steps = new RecordingProgress<PublishStep>();

        // Act
        await _bed.PublishAsync(pack, progress: steps);

        // Assert
        steps.Reports.Should().Equal(PublishStep.Fork, PublishStep.Commit, PublishStep.PullRequest);
    }

    [Fact]
    public async Task The_pull_request_describes_the_pack()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        var pr = _bed.Api.PullRequests.Single();
        pr.Title.Should().Be("New pack «Claude Code translation» · ru · Portal 2");
        pr.Body.Should().Contain("| Pack | `games/steam-620/ru/claude-code` |").And.Contain("| Game build | 1000 (1 file) |").And.Contain("| Tried in game | yes |").And.Contain("CC-BY-SA-4.0");
    }

    [Fact]
    public async Task The_local_report_records_the_pull_request()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Store.Find(PublishBed.Game, "ru", "claude-code")!.Report.Published.Should().Be(new LocalPackPublication(
            "ailocalpack/registry", "claude-code", 1, 1, new Uri("https://github.com/ailocalpack/registry/pull/1"), Fork, Branch, PublishBed.Now));
    }

    [Fact]
    public async Task An_existing_fork_is_reused_whatever_its_name()
    {
        // Arrange
        _bed.Api.AddFork("octo/registry-1", PublishBed.Registry);
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Published>().Which.Fork.Should().Be("octo/registry-1");
        _bed.Api.HasRepo(Fork).Should().BeFalse();
    }

    [Fact]
    public async Task A_new_fork_is_waited_for_until_its_branch_can_be_read()
    {
        // Arrange
        _bed.Api.ForkReadyAfter = 3;
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Published>();
        _bed.Waits.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task A_fork_that_never_becomes_ready_stops_with_a_plain_message()
    {
        // Arrange
        _bed.Api.ForkReadyAfter = int.MaxValue;
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Should().BeEquivalentTo(new
        {
            Step = PublishStep.Fork,
            Problem = PublishProblem.ForkNotReady,
            Message = "GitHub is still making your fork octo/registry; publish again in a few minutes.",
        });
    }

    [Fact]
    public async Task The_fork_is_synced_with_the_registry()
    {
        // Arrange
        _bed.Api.AddFork(Fork, PublishBed.Registry);
        _bed.AddRegistryPack();
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.Files(Fork, "master")!.Keys.Should().Contain("games/steam-620/ru/full/manifest.json");
    }

    [Fact]
    public async Task A_branch_left_by_an_earlier_try_starts_again_from_the_registry()
    {
        // Arrange
        _bed.Api.AddFork(Fork, PublishBed.Registry);
        _bed.Api.Commit(Fork, Branch, new Dictionary<string, string?> { ["junk.txt"] = "half done" }, "Earlier try");
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.History(Fork, Branch).Should().Equal("Claude Code translation: new pack (ru, steam:620)", "Initial commit");
    }

    [Fact]
    public async Task A_branch_with_an_open_pull_request_gets_the_fix_on_top()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        await _bed.PublishAsync(pack);
        _bed.Clock.Advance(TimeSpan.FromHours(1));

        // Act
        var result = await _bed.PublishAsync(_bed.Store.Find(PublishBed.Game, "ru", "claude-code")!);

        // Assert
        result.Should().BeOfType<PublishResult.Published>().Which.Resubmitted.Should().BeTrue();
        _bed.Api.History(Fork, Branch).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_fix_goes_to_the_same_pull_request()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        await _bed.PublishAsync(pack);
        _bed.Clock.Advance(TimeSpan.FromHours(1));

        // Act
        await _bed.PublishAsync(_bed.Store.Find(PublishBed.Game, "ru", "claude-code")!);

        // Assert
        _bed.Api.PullRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_fork_github_wont_sync_gets_the_branch_on_its_own_head()
    {
        // Arrange: the fork exists, then the registry moves on with a change the fork can't take (workflow files).
        _bed.Api.AddFork(Fork, PublishBed.Registry);
        _bed.Api.Commit(PublishBed.Registry, "master", new Dictionary<string, string?> { [".github/workflows/validate.yml"] = "on: pull_request" }, "CI");
        _bed.Api.Fail("POST", "^repos/octo/registry/merge-upstream$", HttpStatusCode.UnprocessableEntity, """{"message":"refusing to allow an OAuth App to create or update workflow without workflow scope"}""");

        // Act
        var result = await _bed.PublishAsync(_bed.SaveLocal());

        // Assert
        (result is PublishResult.Published, _bed.Api.History(Fork, Branch)).Should().BeEquivalentTo((true, new[] { "Claude Code translation: new pack (ru, steam:620)", "Initial commit" }), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_resubmit_rebuilds_the_branch_on_the_registrys_head()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        await _bed.PublishAsync(pack);
        _bed.Api.Commit(Fork, Branch, new Dictionary<string, string?> { ["README.md"] = "stray change" }, "Stray");

        // Act
        await _bed.Publisher().PublishAsync(new PublishRequest(pack, new PublishTarget.NewPack(), RegistryRepo.Default, Fresh: true), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        _bed.Api.History(Fork, Branch).Should().Equal("Claude Code translation: new pack (ru, steam:620)", "Initial commit");
    }

    [Fact]
    public async Task A_resubmit_keeps_the_open_pull_request()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        await _bed.PublishAsync(pack);

        // Act
        var result = await _bed.Publisher().PublishAsync(new PublishRequest(pack, new PublishTarget.NewPack(), RegistryRepo.Default, Fresh: true), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<PublishResult.Published>().Which.Resubmitted.Should().BeTrue();
        _bed.Api.PullRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Sending_the_same_files_again_commits_nothing()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        await _bed.PublishAsync(pack);

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Published>().Which.Unchanged.Should().BeTrue();
        _bed.Api.History(Fork, Branch).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_pull_request_opened_meanwhile_is_reused()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        _bed.Api.Fail("POST", "^repos/ailocalpack/registry/pulls$", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for octo:pack/steam-620/ru/claude-code/rev-1."}]}""");
        _bed.Api.Fail("GET", "^repos/ailocalpack/registry/pulls$", HttpStatusCode.OK, "[]");
        var earlier = _bed.Api.OpenPullRequest(PublishBed.Registry, $"octo:{Branch}");

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Published>().Which.PullRequest.Should().Be(earlier.Number);
    }

    [Fact]
    public async Task A_used_up_rate_limit_says_when_to_try_again()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        _bed.Api.Fail("POST", "^repos/ailocalpack/registry/forks$", HttpStatusCode.Forbidden,
            """{"message":"API rate limit exceeded for user ID 1."}""",
            new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = "1794321000" });

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Should().BeEquivalentTo(new
        {
            Step = PublishStep.Fork,
            Problem = PublishProblem.RateLimited,
            RetryAt = DateTimeOffset.FromUnixTimeSeconds(1794321000),
            Message = "GitHub's request limit for your account is used up until 14:30 UTC; publish again after that (nothing is lost).",
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task A_secondary_rate_limit_waits_the_seconds_github_names()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        _bed.Api.Fail("POST", "git/blobs$", HttpStatusCode.Forbidden,
            """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""",
            new Dictionary<string, string> { ["retry-after"] = "60" });

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Should().BeEquivalentTo(new
        {
            Step = PublishStep.Commit,
            Problem = PublishProblem.RateLimited,
            RetryAt = PublishBed.Now.AddSeconds(60),
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task A_refused_pull_request_gives_githubs_reason()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        _bed.Api.Fail("POST", "^repos/ailocalpack/registry/pulls$", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"No commits between master and pack."}]}""");

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Should().BeEquivalentTo(new
        {
            Step = PublishStep.PullRequest,
            Problem = PublishProblem.Rejected,
            Message = "GitHub refused opening the pull request: Validation Failed: No commits between master and pack.",
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task Publishing_again_after_a_failure_finishes_the_job()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        _bed.Api.Fail("POST", "^repos/ailocalpack/registry/pulls$", HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        await _bed.PublishAsync(pack);

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Published>();
        _bed.Api.History(Fork, Branch).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_missing_registry_is_named()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.Publisher().PublishAsync(
            new PublishRequest(pack, new PublishTarget.NewPack(), new RegistryRepo("someone", "nothing")), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Message.Should().Be("The registry someone/nothing isn't on GitHub (or can't be seen with your account).");
    }

    [Fact]
    public async Task Without_a_token_the_player_is_asked_to_sign_in()
    {
        // Arrange
        await _bed.Keychain.DeleteAsync(Core.GitHub.GitHubSession.TokenSecret, TestContext.Current.CancellationToken);
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Problem.Should().Be(PublishProblem.NotSignedIn);
        _bed.Api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_keychain_is_reported()
    {
        // Arrange
        _bed.Keychain.FailWith = SecretResult.Unavailable("no keyring");
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Problem.Should().Be(PublishProblem.Keychain);
    }

    [Fact]
    public async Task A_revoked_token_asks_to_sign_in_again()
    {
        // Arrange
        await _bed.Keychain.WriteAsync(Core.GitHub.GitHubSession.TokenSecret, "gho_revoked", TestContext.Current.CancellationToken);
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Should().BeEquivalentTo(new
        {
            Problem = PublishProblem.Unauthorized,
            Message = "GitHub no longer accepts your sign-in (access revoked or expired); sign in to GitHub again.",
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task A_new_pack_whose_id_is_taken_gets_the_next_free_one()
    {
        // Arrange
        _bed.AddRegistryPack("claude-code");
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Published>().Which.Manifest.Id.Should().Be("claude-code-2");
    }

    [Fact]
    public async Task A_new_revision_credits_the_player_after_the_original_author()
    {
        // Arrange
        _bed.AddRegistryPack();
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack, new PublishTarget.RevisionOf("full"));

        // Assert
        var manifest = _bed.CommittedManifest(Fork, "pack/steam-620/ru/full/rev-2", "games/steam-620/ru/full");
        manifest.Authors.Should().Equal(new PackAuthor("alice", "Alice"), new PackAuthor("octo", "Octo Cat"));
        manifest.Revisions.Select(r => (r.Number, r.By)).Should().Equal((1, "alice"), (2, "octo"));
    }

    [Fact]
    public async Task A_new_revision_has_a_pull_request_title_of_its_own()
    {
        // Arrange
        _bed.AddRegistryPack();
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack, new PublishTarget.RevisionOf("full"));

        // Assert
        _bed.Api.PullRequests.Single().Title.Should().Be("Revision 2 of «Full translation» · ru · Portal 2");
    }

    [Fact]
    public async Task A_new_revision_removes_files_the_pack_no_longer_has()
    {
        // Arrange
        _bed.AddRegistryPack("full", "strings/old.json.gz");
        var pack = _bed.SaveLocal();

        // Act
        await _bed.PublishAsync(pack, new PublishTarget.RevisionOf("full"));

        // Assert
        _bed.Api.Files(Fork, "pack/steam-620/ru/full/rev-2")!.Keys.Where(k => k.StartsWith("games/", StringComparison.Ordinal))
            .Should().BeEquivalentTo("games/steam-620/ru/full/manifest.json", "games/steam-620/ru/full/strings/strings.json.gz");
    }

    [Fact]
    public async Task A_revision_of_a_pack_the_registry_no_longer_has_is_refused()
    {
        // Arrange
        var pack = _bed.SaveLocal();

        // Act
        var result = await _bed.PublishAsync(pack, new PublishTarget.RevisionOf("gone"));

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Should().BeEquivalentTo(new { Step = PublishStep.Commit, Problem = PublishProblem.TargetGone }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task A_pack_the_registry_would_refuse_is_not_sent()
    {
        // Arrange
        var pack = _bed.SaveLocal(PublishBed.LocalManifest() with { Dependencies = [new PackDependency("bepinex", "5.4.23.2")] });

        // Act
        var result = await _bed.PublishAsync(pack);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Problem.Should().Be(PublishProblem.Invalid);
        _bed.Api.Calls.Should().NotContain(c => c.StartsWith("POST repos/octo/registry/git/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_game_whose_publisher_opted_out_is_refused()
    {
        // Arrange
        var pack = _bed.SaveLocal();
        var index = new RegistryIndex(PublishBed.Now, new Dictionary<GameId, RegistryGame>
        {
            [PublishBed.Game] = new(PublishBed.Game, OptOut: true, AntiCheatAllowed: false, new Dictionary<string, IReadOnlyList<RegistryPack>>()),
        });

        // Act
        var result = await _bed.Publisher().PublishAsync(
            new PublishRequest(pack, new PublishTarget.NewPack(), RegistryRepo.Default, index), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<PublishResult.Failed>().Which.Problem.Should().Be(PublishProblem.OptedOut);
    }

    [Fact]
    public async Task A_patch_archive_is_uploaded_to_the_forks_patches_release()
    {
        // Arrange
        var pack = SavePatchPack(out var archive);

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.Assets(Fork, "patches").Should().ContainKey("steam-620-ru-claude-code-1.zip").WhoseValue.Should().Equal(archive);
    }

    [Fact]
    public async Task A_patch_packs_url_names_the_registrys_release_asset()
    {
        // Arrange
        var pack = SavePatchPack(out _);

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.CommittedManifest(Fork, Branch, Folder).Patch!.Url.Should().Be(new Uri("https://github.com/ailocalpack/registry/releases/download/patches/steam-620-ru-claude-code-1.zip"));
    }

    [Fact]
    public async Task An_uploaded_archive_with_the_same_digest_is_not_sent_again()
    {
        // Arrange
        var pack = SavePatchPack(out _);
        await _bed.PublishAsync(pack);

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.Calls.Count(c => c.StartsWith("POST uploads/", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task The_pull_request_says_where_ci_finds_the_patch_archive()
    {
        // Arrange
        var pack = SavePatchPack(out _);

        // Act
        await _bed.PublishAsync(pack);

        // Assert
        _bed.Api.PullRequests.Single().Body.Should().Contain("https://github.com/octo/registry/releases/download/patches/steam-620-ru-claude-code-1.zip");
    }

    private LocalPack SavePatchPack(out byte[] archive)
    {
        archive = [0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var file = Path.Combine(_bed.Paths.Cache, "patch.zip");
        Directory.CreateDirectory(_bed.Paths.Cache);
        File.WriteAllBytes(file, archive);
        var manifest = PublishBed.LocalManifest() with
        {
            Recipe = null,
            Fonts = [],
            Patch = new PackPatch(
                PackPatch.VcdiffFormat,
                LocalPackStore.PatchUrl(PublishBed.Game, "ru", "claude-code", 1),
                Convert.ToHexStringLower(SHA256.HashData(archive)),
                archive.Length,
                new Dictionary<string, string> { ["data/Items.json"] = new('c', 64) }),
        };
        return _bed.Store.Save(manifest, PublishBed.Table, PublishBed.Report(), patchArchive: file);
    }
}
