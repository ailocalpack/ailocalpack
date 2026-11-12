using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Desktop.Ratings;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Ratings;

/// <summary>The rating block in Packs details, the game page's prompt and what they send (task 63).</summary>
public sealed class RatingTests
{
    // ---------- Played with ----------

    [Fact]
    public async Task Closing_the_game_after_playing_with_the_pack_marks_it_played()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        var page = bed.Page();

        // Act
        await bed.PlayAndCloseAsync(page);

        // Assert
        await RatingBed.Until(() => bed.Store.Find(InstallBed.Alpha, "ru", "full") is not null);
        bed.Store.Find(InstallBed.Alpha, "ru", "full").Should().BeEquivalentTo(new { Revision = 1, IsTried = true });
    }

    // ---------- Rating block ----------

    [Fact]
    public async Task The_block_shows_once_the_player_played_with_the_pack()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        var page = bed.Page();

        // Act
        bed.Played();

        // Assert
        page.Packs.Rows.Single().ShowRating.Should().BeTrue();
    }

    [Fact]
    public async Task A_pack_that_is_not_installed_has_no_block()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync(installed: false);

        // Act
        var page = bed.Page();

        // Assert
        page.Packs.Rows.Single().Rating.Should().BeNull();
    }

    [Fact]
    public async Task The_author_gets_like_but_no_confirmation_buttons()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.GitHub.Account = FakeGitHubHost.Octocat with { Login = "Alice" };
        bed.Played();

        // Act
        var block = RatingBed.Block(bed.Page())!;

        // Assert
        (block.ShowConfirm, block.ShowComment, block.LikeCommand.CanExecute(null), block.StatusText).Should().Be(
            (false, false, true, "You're credited on this translation, so you can't confirm it works: other players do."));
    }

    [Fact]
    public async Task Without_a_discussion_nothing_can_be_sent()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync(discussion: false);
        bed.Played();

        // Act
        var block = RatingBed.Block(bed.Page())!;

        // Assert
        (block.WorksCommand.CanExecute(null), block.LikeCommand.CanExecute(null), block.StatusText).Should().Be(
            (false, false, "Rating opens once the registry creates this translation's Discussion on GitHub."));
    }

    [Fact]
    public async Task Works_sends_the_report_with_the_revision_build_and_comment()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;
        block.Comment = "Шрифты в меню мелкие";

        // Act
        block.WorksCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => bed.Discussions.Reports.Count > 0);
        var (discussion, report) = bed.Discussions.Reports.Should().ContainSingle().Subject;
        (discussion, report with { App = null }).Should().Be((
            new DiscussionRef("ailocalpack", "registry", 42),
            new PackReport(PackVerdict.Works, InstallBed.Alpha, "ru", "full", 1, "1000", "Шрифты в меню мелкие")));
    }

    [Fact]
    public async Task A_sent_report_is_remembered_and_shown()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.DoesNotWorkCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => block.IsDoesNotWorkReported);
        (block.IsDoesNotWorkReported, block.StatusText).Should().Be(
            (true, $"You reported «Doesn't work» for rev 1 on {bed.Clock.GetUtcNow().ToLocalTime().ToString("d", bed.Services.GetRequiredService<Localization.Localizer>().Culture)}. Sending again replaces your report."));
    }

    [Fact]
    public async Task A_sent_report_thanks_the_player_with_a_link_to_the_comment()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.WorksCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => bed.Toasts.Items.Any(t => t.Key == "rating:steam:10"));
        bed.Toasts.Items.Should().ContainSingle(t => t.Text == "Thanks! Your report was sent to the translation's GitHub Discussion.").Which.Tone.Should().Be(ToastTone.Ok);
    }

    [Fact]
    public async Task Like_gives_the_thumbs_up_and_turns_into_liked()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.LikeCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => block.IsLiked);
        (bed.Discussions.Likes.Single().Like, block.LikeText).Should().Be((true, "Liked"));
    }

    [Fact]
    public async Task Liked_takes_the_thumbs_up_back()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        bed.Discussions.State = bed.Discussions.State with { ViewerLiked = true };
        var block = RatingBed.Block(bed.Page())!;
        await RatingBed.Until(() => block.IsLiked);

        // Act
        block.LikeCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => !block.IsLiked);
        bed.Discussions.Likes.Single().Like.Should().BeFalse();
    }

    [Fact]
    public async Task While_sending_the_buttons_are_off()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        bed.Discussions.Hold();
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.WorksCommand.Execute(null);

        // Assert
        (block.IsSending, block.LikeCommand.CanExecute(null), block.StatusText).Should().Be((true, false, "Sending to GitHub…"));
        bed.Discussions.Release();
    }

    [Fact]
    public async Task A_failed_send_says_why()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        bed.Discussions.Error = new RatingError(RatingProblem.Network, "offline");
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.WorksCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => block.IsFailed);
        (block.IsFailed, block.StatusText, block.WorksCommand.CanExecute(null)).Should().Be(
            (true, "Can't reach GitHub. Check the connection and try again.", true));
    }

    [Fact]
    public async Task Signed_out_the_sign_in_dialog_comes_first_and_nothing_is_sent()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync(signedIn: false);
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.LikeCommand.Execute(null);

        // Assert
        (bed.Dialog is RatingSignInDialogViewModel, bed.Discussions.Likes.Count).Should().Be((true, 0));
    }

    [Fact]
    public async Task Once_GitHub_confirms_the_dialog_closes_and_the_like_is_sent()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync(signedIn: false);
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;
        block.LikeCommand.Execute(null);
        var dialog = (RatingSignInDialogViewModel)bed.Dialog!;
        await RatingBed.Until(() => dialog.SignIn.IsWaiting);

        // Act
        bed.GitHub.Approve(FakeGitHubHost.Octocat);

        // Assert
        await RatingBed.Until(() => bed.Discussions.Likes.Count > 0);
        (bed.Dialog, bed.Discussions.Likes.Count).Should().Be((null, 1));
    }

    [Fact]
    public async Task Cancelling_the_sign_in_sends_nothing()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync(signedIn: false);
        bed.Played();
        var block = RatingBed.Block(bed.Page())!;
        block.WorksCommand.Execute(null);
        var dialog = (RatingSignInDialogViewModel)bed.Dialog!;

        // Act
        dialog.CancelCommand.Execute(null);

        // Assert
        (bed.Dialog, bed.Discussions.Reports.Count).Should().Be((null, 0));
    }

    [Fact]
    public async Task A_revoked_sign_in_signs_out_and_asks_again()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        bed.Discussions.Error = new RatingError(RatingProblem.Unauthorized, "Bad credentials");
        var block = RatingBed.Block(bed.Page())!;

        // Act
        block.WorksCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => bed.Dialog is not null);
        (bed.GitHub.Account is null, bed.Dialog is RatingSignInDialogViewModel).Should().Be((true, true));
    }

    [Fact]
    public async Task Showing_the_block_reads_the_players_earlier_report_from_GitHub()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var report = new PackReport(PackVerdict.Works, InstallBed.Alpha, "ru", "full", 1, "1000");
        bed.Discussions.State = bed.Discussions.State with { ViewerLiked = true, Reports = [new DiscussionReport("DC_1", FakePackDiscussions.CommentUrl, report, ByViewer: true)] };

        // Act
        var block = RatingBed.Block(bed.Page())!;

        // Assert
        await RatingBed.Until(() => block.IsWorksReported);
        (block.IsLiked, block.IsWorksReported).Should().Be((true, true));
    }

    // ---------- Prompt ----------

    [Fact]
    public async Task The_prompt_shows_after_the_first_session()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        var page = bed.Page();

        // Act
        bed.Played();

        // Assert
        (page.ShowRatingPrompt, page.RatingPromptText).Should().Be((true, "How is «full»? Your answer helps other players."));
    }

    [Fact]
    public async Task Later_hides_the_prompt()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var page = bed.Page();

        // Act
        page.RateLaterCommand.Execute(null);

        // Assert
        page.ShowRatingPrompt.Should().BeFalse();
    }

    [Fact]
    public async Task The_prompts_works_sends_the_report_and_hides_the_prompt()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var page = bed.Page();

        // Act
        page.RateWorksCommand.Execute(null);

        // Assert
        await RatingBed.Until(() => !page.ShowRatingPrompt);
        (page.ShowRatingPrompt, bed.Discussions.Reports.Single().Report.Text).Should().Be((false, null));
    }

    [Fact]
    public async Task The_prompts_thanks_toast_offers_to_add_details_in_packs()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var page = bed.Page(GamePageTab.Generation);
        page.RateDoesNotWorkCommand.Execute(null);
        await RatingBed.Until(() => bed.Toasts.Items.Any(t => t.Key == "rating:steam:10"));

        // Act
        bed.Toasts.Items.Single(t => t.Key == "rating:steam:10").Actions.First(a => a.Text == "Add details").Command.Execute(null);

        // Assert
        (page.SelectedTab, page.Packs.Selected?.Pack.Id, page.Packs.Selected?.ShowRating).Should().Be((GamePageTab.Packs, "full", true));
    }

    [Fact]
    public async Task The_author_is_not_prompted()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.GitHub.Account = FakeGitHubHost.Octocat with { Login = "alice" };
        bed.Played();

        // Act
        var page = bed.Page();

        // Assert
        page.ShowRatingPrompt.Should().BeFalse();
    }

    [Fact]
    public async Task The_menus_rate_opens_the_installed_pack_in_packs()
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();
        var page = bed.Page(GamePageTab.Generation);

        // Act
        page.RateCommand.Execute(null);

        // Assert
        (page.SelectedTab, page.Packs.Selected?.Pack.Id).Should().Be((GamePageTab.Packs, "full"));
    }
}
