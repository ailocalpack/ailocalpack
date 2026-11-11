using AiLocalPack.Core.Publishing;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>
/// The publish strip and the Mine row (task 61, design §7): what each state says, which buttons it has, and how the
/// result's share strip and Mine follow a publish.
/// </summary>
public sealed class PublishStripTests
{
    private static readonly DateTimeOffset Tried = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);

    private static GamePageViewModel Page(InstallBed bed, GamePageTab tab) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, tab);

    private static void SignedInAndLicensed(InstallBed bed)
    {
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { PublishLicenseAccepted = true });
    }

    private static Task SendAsync(InstallBed bed) =>
        bed.Services.GetRequiredService<PublishFlow>().SendAsync(bed.Game, bed.Services.GetRequiredService<LocalPacks>().Find(InstallBed.Alpha, "ru", "claude-code")!, new PublishTarget.NewPack());

    [Fact]
    public async Task A_tried_pack_offers_the_share_strip_with_Publish_on()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);

        // Act
        var result = Page(bed, GamePageTab.Generation).Generation.Result!;

        // Assert
        (result.ShowShare, result.Publish.ShowStatus, result.Publish.PublishCommand.CanExecute(null)).Should().Be((true, false, true));
    }

    [Fact]
    public async Task A_pack_not_tried_has_no_Publish_in_Mine()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var row = Page(bed, GamePageTab.Packs).Packs.Mine.Single();

        // Assert
        (row.ShowPublish, row.PublishCommand.CanExecute(null), row.ActionHint).Should().Be((false, false, "Try it in game first: publishing opens after that."));
    }

    [Fact]
    public async Task Once_sent_the_result_shows_CI_running_instead_of_the_share_strip()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        var page = Page(bed, GamePageTab.Generation);

        // Act
        await SendAsync(bed);

        // Assert
        var publish = page.Generation.Result!.Publish;
        (page.Generation.Result.ShowShare, publish.IsChecking, publish.Title, publish.ShowOpenPr, publish.ShowPublish)
            .Should().Be((false, true, "PR #7 opened · CI is checking the translation", true, false));
    }

    [Fact]
    public async Task Once_sent_the_Mine_row_reads_the_PR_and_CI()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        var page = Page(bed, GamePageTab.Packs);

        // Act
        await SendAsync(bed);

        // Assert
        var row = page.Packs.Mine.Single();
        (row.Meta, row.ShowPublish, row.ShowPublishNote).Should().Be(("PR #7 · CI running", false, false));
    }

    [Fact]
    public async Task Open_PR_opens_it_in_the_browser()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        var page = Page(bed, GamePageTab.Generation);
        await SendAsync(bed);

        // Act
        page.Generation.Result!.Publish.OpenPrCommand.Execute(null);

        // Assert
        bed.Services.GetRequiredService<FakeShell>().OpenedUrls.Should().Equal(FakePackPublishing.PrUrl);
    }

    [Fact]
    public async Task A_failed_try_says_why_and_offers_Publish_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        bed.Services.GetRequiredService<FakePackPublishing>().Result = _ =>
            new PublishResult.Failed(PublishStep.Commit, PublishProblem.RateLimited, "limit");
        var page = Page(bed, GamePageTab.Generation);

        // Act
        await SendAsync(bed);

        // Assert
        var publish = page.Generation.Result!.Publish;
        (publish.IsFailed, publish.Title, publish.Text, publish.PublishText, publish.PublishCommand.CanExecute(null))
            .Should().Be((true, "Couldn't publish «Claude Code translation»", "GitHub's request limit for your account is used up for now; publish again in a few minutes (nothing is lost).",
                "Publish again", true));
    }

    [Fact]
    public async Task Merged_into_the_index_it_is_published_with_the_confirmations_meter()
    {
        // Arrange: CI merged it; the index lists the pack with one "works" confirmation and its Discussion
        var discussion = new Uri("https://github.com/ailocalpack/registry-test/discussions/3");
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        await SendAsync(bed);
        bed.Registry.Pack(10, "ru", "claude-code", discussion: discussion).Write();
        await bed.Services.GetRequiredService<PackIndex>().RefreshAsync();

        // Act
        var publish = Page(bed, GamePageTab.Generation).Generation.Result!.Publish;

        // Assert
        (publish.IsPublished, publish.Title, publish.MeterText, publish.MeterBoxes.Count, publish.Link, publish.HasDiscussion)
            .Should().Be((true, "Published: «claude-code» · alpha", "0 of 3 players confirmed “works”", 3, discussion, true));
    }

    [Fact]
    public async Task Copy_link_copies_the_Discussion()
    {
        // Arrange
        var discussion = new Uri("https://github.com/ailocalpack/registry-test/discussions/3");
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        await SendAsync(bed);
        bed.Registry.Pack(10, "ru", "claude-code", discussion: discussion).Write();
        await bed.Services.GetRequiredService<PackIndex>().RefreshAsync();
        var publish = Page(bed, GamePageTab.Generation).Generation.Result!.Publish;

        // Act
        await publish.CopyLinkCommand.ExecuteAsync(null);

        // Assert
        bed.Services.GetRequiredService<FakeShell>().Copied.Should().Equal(discussion.AbsoluteUri);
    }

    [Fact]
    public async Task While_another_pack_is_sent_Publish_is_off()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        var publisher = bed.Services.GetRequiredService<FakePackPublishing>();
        publisher.Hold();
        var page = Page(bed, GamePageTab.Generation);

        // Act
        var sending = SendAsync(bed);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (page.Generation.Result!.Publish.Status.Run?.Step != PublishStep.PullRequest && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Assert
        var publish = page.Generation.Result!.Publish;
        (publish.IsSending, publish.Steps.Select(s => (s.IsDone, s.IsCurrent)), publish.PublishCommand.CanExecute(null))
            .Should().BeEquivalentTo((true, new[] { (true, false), (true, false), (false, true) }, false));
        publisher.Release();
        await sending;
    }
}
