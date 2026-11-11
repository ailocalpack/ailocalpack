using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Install;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>
/// Following the player's pack PRs (task 62): what a read does to the pack's state, the Activity decision and feed, the
/// report, and when the PR is read again.
/// </summary>
public sealed class PullRequestTrackerTests
{
    private static readonly DateTimeOffset Tried = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
    private static readonly Uri PrUrl = new("https://github.com/ailocalpack/registry-test/pull/7");

    private static readonly CiAnnotation NotAlpha = new(
        "games/steam-10/ru/claude-code/manifest.json", CiAnnotationLevel.Failure, "status", "a new pack or revision must have \"status\": \"alpha\" (found \"verified\")");

    private static LocalPack Sent(InstallBed bed, PullRequestState state = PullRequestState.Open)
    {
        bed.LocalPack(tried: Tried);
        return bed.Services.GetRequiredService<LocalPackStore>().UpdateReport(InstallBed.Alpha, "ru", "claude-code", r => r with
        {
            Published = new LocalPackPublication("ailocalpack/registry-test", "claude-code", 1, 7, PrUrl, "octocat/registry-test", "pack/steam-10/ru/claude-code/rev-1", Tried.AddHours(1), state),
        })!;
    }

    private static PullRequestTracker Tracker(InstallBed bed) => bed.Services.GetRequiredService<PullRequestTracker>();

    private static FakePullRequestStatus GitHub(InstallBed bed) => bed.Services.GetRequiredService<FakePullRequestStatus>();

    private static ActivityStore Activity(InstallBed bed) => bed.Services.GetRequiredService<ActivityStore>();

    private static LocalPack Fresh(InstallBed bed) => bed.Services.GetRequiredService<LocalPacks>().Find(InstallBed.Alpha, "ru", "claude-code")!;

    private static PublishStatus StatusOf(InstallBed bed) => bed.Services.GetRequiredService<PublishFlow>().StatusOf(Fresh(bed));

    [Fact]
    public async Task An_open_pr_is_read_from_the_registry_it_went_to()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        GitHub(bed).Reads.Should().Equal((new RegistryRepo("ailocalpack", "registry-test"), 7));
    }

    [Fact]
    public async Task A_rejected_pr_makes_the_pack_ci_failed_with_the_reason()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        StatusOf(bed).Should().Match<PublishStatus>(s => s.Phase == PublishPhase.CiFailed && s.CiFailure!.Kind == CiFailureKind.Status);
    }

    [Fact]
    public async Task A_rejected_pr_is_a_decision_in_activity_with_the_reason()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        Activity(bed).Decisions.Should().ContainSingle().Which.Should().Match<ActivityEvent>(e =>
            e.Kind == ActivityEventKind.PullRequestFailed
            && e.PullRequest == 7
            && e.Link == PrUrl
            && e.Detail!.StartsWith("The manifest isn't marked alpha (status: a new pack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_rejected_head_read_again_adds_no_second_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        Activity(bed).Events.Count(e => e.Kind == ActivityEventKind.PullRequestFailed).Should().Be(1);
    }

    [Fact]
    public async Task A_new_head_rejected_again_is_a_new_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h2", NotAlpha);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        Activity(bed).Events.Count(e => e.Kind == ActivityEventKind.PullRequestFailed).Should().Be(2);
    }

    [Fact]
    public async Task Ci_passing_after_a_fix_on_github_settles_the_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Pending, "h2");

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        (Activity(bed).Decisions.Count, StatusOf(bed).Phase).Should().Be((0, PublishPhase.Checking));
    }

    [Fact]
    public async Task A_merged_pr_is_recorded_in_the_report()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Merged, CiState.Passed);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        Fresh(bed).Report.Published.Should().Match<LocalPackPublication>(p => p.State == PullRequestState.Merged && p.StateAt == DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task A_merged_pr_is_a_feed_event_and_the_pack_reads_merged()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Merged, CiState.Passed);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        (Activity(bed).Feed.Single().Kind, StatusOf(bed).Phase).Should().Be((ActivityEventKind.PackMerged, PublishPhase.Merged));
    }

    [Fact]
    public async Task A_merge_settles_the_ci_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);
        GitHub(bed).Answer(7, PullRequestState.Merged, CiState.Passed);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        Activity(bed).Decisions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_closed_pr_is_recorded_and_offers_Publish_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Closed, CiState.Passed);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        (Fresh(bed).Report.Published!.State, Activity(bed).Feed.Single().Kind, PublishStatuses.CanPublish(Fresh(bed), StatusOf(bed)))
            .Should().Be((PullRequestState.Closed, ActivityEventKind.PullRequestClosed, true));
    }

    [Fact]
    public async Task A_merged_pr_is_not_read_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed, PullRequestState.Merged);

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        GitHub(bed).Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pr_read_moments_ago_is_not_due_yet()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Act
        await Tracker(bed).CheckAsync(all: false, TestContext.Current.CancellationToken);

        // Assert
        GitHub(bed).Reads.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_read_backs_off_before_reading_again()
    {
        // Arrange
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var bed = await InstallBed.CreateAsync(configure: s => s.AddSingleton<TimeProvider>(clock));
        Sent(bed);
        GitHub(bed).Answer(7, new PullRequestCheckResult.Failed(new GitHubApiError(PublishProblem.Network, 0, "offline")));
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1.5));

        // Act
        await Tracker(bed).CheckAsync(all: false, TestContext.Current.CancellationToken);

        // Assert: two errors in a row wait 2 minutes, so 1.5 minutes later it isn't read.
        GitHub(bed).Reads.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_pack_checking()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, new PullRequestCheckResult.Failed(new GitHubApiError(PublishProblem.Network, 0, "offline")));

        // Act
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        StatusOf(bed).Phase.Should().Be(PublishPhase.Checking);
    }

    [Fact]
    public async Task Sending_again_drops_the_old_ci_result()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        Sent(bed);
        GitHub(bed).Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);
        await Tracker(bed).CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Act
        Tracker(bed).Sent(Fresh(bed));

        // Assert
        (StatusOf(bed).Phase, Activity(bed).Decisions.Count).Should().Be((PublishPhase.Checking, 0));
    }

    [Fact]
    public async Task A_published_pack_the_index_lists_as_verified_is_one_feed_event()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(packs: b => b.Registry.Pack(10, "ru", "claude-code", status: PackStatus.Verified));
        Sent(bed, PullRequestState.Merged);
        _ = Tracker(bed);
        await bed.Services.GetRequiredService<PackIndex>().RefreshAsync();

        // Act
        await bed.Services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        Activity(bed).Feed.Should().ContainSingle(e => e.Kind == ActivityEventKind.PackVerified);
    }
}
