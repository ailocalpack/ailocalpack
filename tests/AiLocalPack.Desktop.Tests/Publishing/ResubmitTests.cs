using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Settings.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>
/// A PR registry CI rejected, merged or closed (task 62): the strip, the Mine row, <c>Fix and resubmit</c> from the strip
/// and from Activity, and Settings → GitHub → My published packs.
/// </summary>
public sealed class ResubmitTests
{
    private static readonly DateTimeOffset Tried = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
    private static readonly Uri PrUrl = FakePackPublishing.PrUrl;

    private static readonly CiAnnotation NotAlpha = new(
        "games/steam-10/ru/claude-code/manifest.json", CiAnnotationLevel.Failure, "status", "must be alpha");

    private static void SignedInAndLicensed(InstallBed bed)
    {
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { PublishLicenseAccepted = true });
    }

    /// <summary>The pack sent as PR #7 (revision <paramref name="revision"/>), then read by the tracker as GitHub answers.</summary>
    private static async Task SentAsync(InstallBed bed, PullRequestState state, CiState ci, int revision = 1)
    {
        bed.LocalPack(tried: Tried);
        bed.Services.GetRequiredService<LocalPackStore>().UpdateReport(InstallBed.Alpha, "ru", "claude-code", r => r with
        {
            Published = new LocalPackPublication("ailocalpack/registry-test", revision > 1 ? "neon" : "claude-code", revision, 7, PrUrl, "octocat/registry-test", "b", Tried.AddHours(1)),
        });
        bed.Services.GetRequiredService<GenerationService>().Recover();
        var github = bed.Services.GetRequiredService<FakePullRequestStatus>();
        if (ci == CiState.Failed)
        {
            github.Answer(7, state, ci, "h1", NotAlpha);
        }
        else
        {
            github.Answer(7, state, ci);
        }

        await bed.Services.GetRequiredService<PullRequestTracker>().CheckAsync(all: true, TestContext.Current.CancellationToken);
    }

    private static GamePageViewModel Page(InstallBed bed, GamePageTab tab) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, tab);

    private static PackPublishViewModel Strip(InstallBed bed) => Page(bed, GamePageTab.Generation).Generation.Result!.Publish;

    private static LocalPack Fresh(InstallBed bed) => bed.Services.GetRequiredService<LocalPacks>().Find(InstallBed.Alpha, "ru", "claude-code")!;

    private static FakePackPublishing Publisher(InstallBed bed) => bed.Services.GetRequiredService<FakePackPublishing>();

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // ---------- Strip and Mine ----------

    [Fact]
    public async Task A_rejected_pr_shows_the_reason_with_Open_PR_and_Fix_and_resubmit()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);

        // Act
        var strip = Strip(bed);

        // Assert
        (strip.IsBad, strip.Title, strip.Text, strip.ShowOpenPr, strip.ShowResubmit, strip.ShowPublishAgain).Should().Be((
            true,
            "CI rejected PR #7",
            "The manifest isn't marked alpha (status: must be alpha). Fix and resubmit sends it again as alpha.",
            true,
            true,
            false));
    }

    [Fact]
    public async Task A_merged_pr_waits_green_for_the_index()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Merged, CiState.Passed);

        // Act
        var strip = Strip(bed);

        // Assert
        (strip.IsOk, strip.Title, Page(bed, GamePageTab.Packs).Packs.Mine.Single().Meta)
            .Should().Be((true, "PR #7 merged: «Claude Code translation» is in the registry as alpha", "PR #7 merged · alpha"));
    }

    [Fact]
    public async Task A_closed_pr_offers_Publish_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Closed, CiState.Passed);

        // Act
        var strip = Strip(bed);

        // Assert
        (strip.IsBad, strip.Title, strip.ShowPublishAgain, strip.PublishText).Should().Be((true, "PR #7 was closed without merging", true, "Publish again"));
    }

    [Fact]
    public async Task An_open_page_follows_the_tracker()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Open, CiState.Pending);
        var page = Page(bed, GamePageTab.Packs);
        bed.Services.GetRequiredService<FakePullRequestStatus>().Answer(7, PullRequestState.Open, CiState.Failed, "h1", NotAlpha);

        // Act
        await bed.Services.GetRequiredService<PullRequestTracker>().CheckAsync(all: true, TestContext.Current.CancellationToken);

        // Assert
        page.Packs.Mine.Single().Meta.Should().Be("PR #7 · CI failed");
    }

    // ---------- Fix and resubmit ----------

    [Fact]
    public async Task Fix_and_resubmit_sends_the_pack_fresh_to_the_same_target()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        SignedInAndLicensed(bed);
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);

        // Act
        Strip(bed).ResubmitCommand.Execute(null);
        await Until(() => Publisher(bed).Requests.Count > 0 && !bed.Services.GetRequiredService<PublishFlow>().IsBusy);

        // Assert
        Publisher(bed).Requests.Single().Should().Match<PublishRequest>(r => r.Fresh && r.Target is PublishTarget.NewPack);
    }

    [Fact]
    public async Task Fix_and_resubmit_of_a_revision_goes_to_the_same_pack()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        SignedInAndLicensed(bed);
        await SentAsync(bed, PullRequestState.Open, CiState.Failed, revision: 4);

        // Act
        bed.Services.GetRequiredService<PublishFlow>().Resubmit(bed.Game, Fresh(bed));
        await Until(() => Publisher(bed).Requests.Count > 0 && !bed.Services.GetRequiredService<PublishFlow>().IsBusy);

        // Assert
        Publisher(bed).Requests.Single().Target.Should().Be(new PublishTarget.RevisionOf("neon"));
    }

    [Fact]
    public async Task After_resubmitting_ci_runs_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        SignedInAndLicensed(bed);
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);

        // Act
        bed.Services.GetRequiredService<PublishFlow>().Resubmit(bed.Game, Fresh(bed));
        await Until(() => bed.Services.GetRequiredService<PublishFlow>().StatusOf(Fresh(bed)).Phase == PublishPhase.Checking);

        // Assert
        (Publisher(bed).Requests.Count, bed.Services.GetRequiredService<PublishFlow>().StatusOf(Fresh(bed)).Phase).Should().Be((1, PublishPhase.Checking));
    }

    [Fact]
    public async Task Signed_out_Fix_and_resubmit_asks_for_GitHub_first()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);

        // Act
        bed.Services.GetRequiredService<PublishFlow>().Resubmit(bed.Game, Fresh(bed));

        // Assert
        (bed.Services.GetRequiredService<IDialogService>().Current, Publisher(bed).Requests.Count)
            .Should().Match<(DialogViewModel?, int)>(x => x.Item1 is PublishSignInDialogViewModel && x.Item2 == 0);
    }

    [Fact]
    public async Task A_pr_ci_is_still_checking_is_not_resubmitted()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        SignedInAndLicensed(bed);
        await SentAsync(bed, PullRequestState.Open, CiState.Pending);

        // Act
        bed.Services.GetRequiredService<PublishFlow>().Resubmit(bed.Game, Fresh(bed));

        // Assert
        Publisher(bed).Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resubmitted_fix_is_worded_as_pushed_to_the_same_pr()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        SignedInAndLicensed(bed);
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);
        var manifest = Fresh(bed).Manifest;
        Publisher(bed).Result = _ => new PublishResult.Published(7, PrUrl, manifest, "octocat/registry-test", "b", Resubmitted: true, Unchanged: false);

        // Act
        await bed.Services.GetRequiredService<PublishFlow>().SendAsync(bed.Game, Fresh(bed), new PublishTarget.NewPack(), fresh: true);

        // Assert
        bed.Services.GetRequiredService<ActivityStore>().Feed.First(e => e.Kind == ActivityEventKind.PackSubmitted).Detail
            .Should().Be("Fix pushed to PR #7 · CI checks the translation again");
    }

    // ---------- Activity ----------

    [Fact]
    public async Task The_activity_card_offers_Fix_and_resubmit_and_the_PR()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);

        // Act
        var card = bed.Services.GetRequiredService<ActivityPanelViewModel>().Decisions.Single();

        // Assert
        (card.Title, card.Actions.Select(a => a.Text)).Should().BeEquivalentTo(("Registry CI rejected PR #7 of «Claude Code translation»", new[] { "Fix and resubmit", "PR on GitHub" }));
    }

    [Fact]
    public async Task Fix_and_resubmit_on_the_activity_card_sends_the_pack_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        SignedInAndLicensed(bed);
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);
        var card = bed.Services.GetRequiredService<ActivityPanelViewModel>().Decisions.Single();

        // Act
        card.Actions[0].Command.Execute(null);
        await Until(() => Publisher(bed).Requests.Count > 0 && !bed.Services.GetRequiredService<PublishFlow>().IsBusy);

        // Assert
        (Publisher(bed).Requests.Single().Fresh, bed.Services.GetRequiredService<ActivityStore>().Decisions.Count).Should().Be((true, 0));
    }

    [Fact]
    public async Task PR_on_GitHub_opens_the_pr()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);
        var card = bed.Services.GetRequiredService<ActivityPanelViewModel>().Decisions.Single();

        // Act
        card.Actions[1].Command.Execute(null);

        // Assert
        bed.Services.GetRequiredService<FakeShell>().OpenedUrls.Should().Equal(PrUrl);
    }

    // ---------- Settings → GitHub → My published packs ----------

    [Fact]
    public async Task My_published_packs_lists_the_sent_pack_with_its_pr_state()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await SentAsync(bed, PullRequestState.Open, CiState.Failed);
        var section = bed.Services.GetRequiredService<GitHubSettingsSection>();

        // Act
        section.OnShown();

        // Assert
        section.PublishedPacks.Packs.Single().Should().Match<PublishedPackItem>(p =>
            p.Title == "Claude Code translation" && p.Meta == "Alpha · Русский · rev 1" && p.StatusText == "PR #7 · CI failed" && p.CanResubmit && p.HasPr && p.IsBad);
    }

    [Fact]
    public async Task Without_published_packs_the_empty_state_shows()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        var section = bed.Services.GetRequiredService<GitHubSettingsSection>();

        // Act
        section.OnShown();

        // Assert
        (section.PublishedPacks.IsEmpty, section.PublishedPacks.Text).Should().Be((true, "Sign in to see translations you published."));
    }
}
