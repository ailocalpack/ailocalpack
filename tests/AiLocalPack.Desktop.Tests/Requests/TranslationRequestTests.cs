using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Ratings;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Requests;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Requests;

/// <summary>
/// Translation requests in the app (task 74): the Text tab's ask, card and done strip, the request dialog, the
/// Create with AI form's strip and checkbox, claims of jobs, the library's badge and filter, Activity events.
/// </summary>
public sealed class TranslationRequestTests : IDisposable
{
    private static readonly GameId Alpha = GameId.Steam(10);
    private static readonly RequestKey Key = new(Alpha, "ru");

    private readonly TestRegistry _registry = new();
    private ServiceProvider? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _registry.Dispose();
    }

    private ServiceProvider Services => _services!;

    private FakeRequestDiscussions Discussions => Services.GetRequiredService<FakeRequestDiscussions>();

    private TranslationRequests Requests => Services.GetRequiredService<TranslationRequests>();

    private TranslationRequestStore Store => Services.GetRequiredService<TranslationRequestStore>();

    private Game Game => Services.GetRequiredService<GameLibrary>().Catalog.Find(Alpha)!;

    /// <summary>Steam games 10 "Alpha" and 20 "Beta" (build 1000), target Russian, the registry as the test built it.</summary>
    private async Task BuildAsync(Action<TestRegistry>? registry = null, bool signedIn = true)
    {
        registry?.Invoke(_registry);
        _registry.Write();
        _services = TestServices.Build(steam: s => s.AddGame(10, "Alpha").AddGame(20, "Beta"), registry: _registry.Path);
        Services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        if (signedIn)
        {
            Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        }

        _ = Requests;
        await Services.GetRequiredService<PackIndex>().RefreshAsync();
    }

    private GamePageViewModel Page(GamePageTab tab = GamePageTab.Packs) =>
        Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Game, tab);

    private static GenerationJob Job(GenerationJobStatus status = GenerationJobStatus.Running, GenerationStage stage = GenerationStage.Translate) => new()
    {
        Id = "job1",
        Game = Alpha,
        GameName = "Alpha",
        Language = "ru",
        Agent = "claude",
        Stage = stage,
        Status = status,
        Created = DateTimeOffset.UnixEpoch,
        Updated = DateTimeOffset.UnixEpoch,
    };

    // ---------- Text tab ----------

    [Fact]
    public async Task Empty_text_tab_offers_to_request()
    {
        // Arrange
        await BuildAsync();

        // Act
        var packs = Page().Packs;

        // Assert
        (packs.ShowRequestAsk, packs.Request!.AskButtonText).Should().Be((true, "Request translation"));
    }

    [Fact]
    public async Task Others_asking_turn_the_button_into_me_too()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 12), signedIn: false);

        // Act
        var request = Page().Packs.Request!;

        // Assert
        (request.AskButtonText, request.AskText).Should().Be(("Me too · 12",
            "12 players asked for a translation into Русский. Add your voice: the more players ask, the sooner someone with an AI agent takes it."));
    }

    [Fact]
    public async Task Signed_in_page_reads_the_discussion_for_fresh_votes()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 12));
        Discussions.State = Discussions.State with { Votes = 14 };

        // Act
        var request = Page().Packs.Request!;

        // Assert
        (Discussions.Reads, request.AskButtonText).Should().Be((1, "Me too · 14"));
    }

    [Fact]
    public async Task Ask_opens_the_request_dialog()
    {
        // Arrange
        await BuildAsync();
        var packs = Page().Packs;

        // Act
        packs.Request!.AskCommand.Execute(null);

        // Assert
        Services.GetRequiredService<IDialogService>().Current.Should().BeOfType<RequestDialogViewModel>();
    }

    [Fact]
    public async Task Dialog_names_how_many_already_asked()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 12), signedIn: false);
        Page().Packs.Request!.AskCommand.Execute(null);

        // Act
        var dialog = (RequestDialogViewModel)Services.GetRequiredService<IDialogService>().Current!;

        // Assert
        (dialog.HasOthers, dialog.OthersText).Should().Be((true, "12 players already asked for this. Your request adds your vote and your note."));
    }

    [Fact]
    public async Task Sending_the_dialog_asks_with_the_note()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 12));
        Page().Packs.Request!.AskCommand.Execute(null);
        var dialog = (RequestDialogViewModel)Services.GetRequiredService<IDialogService>().Current!;
        dialog.Note = "Menus first";

        // Act
        dialog.SendCommand.Execute(null);

        // Assert
        Discussions.Asks.Should().Equal((Key, "Alpha", FakeRequestDiscussions.Url, (string?)"Menus first"));
    }

    [Fact]
    public async Task Asked_request_is_recorded_and_thanked()
    {
        // Arrange
        await BuildAsync();

        // Act
        Requests.Ask(Game, "ru", null);

        // Assert
        Store.Find(Key)?.Discussion.Should().Be(FakeRequestDiscussions.Url);
        Services.GetRequiredService<ToastService>().Items.Single().Text.Should().Be("Request sent. You'll be told when someone takes it and when the translation is out.");
    }

    [Fact]
    public async Task Signed_out_ask_signs_in_first()
    {
        // Arrange
        await BuildAsync(signedIn: false);

        // Act
        Requests.Ask(Game, "ru", null);

        // Assert
        (Services.GetRequiredService<IDialogService>().Current as RatingSignInDialogViewModel)?.TitleText.Should().Be("Sign in to GitHub to ask for translations");
        Discussions.Asks.Should().BeEmpty();
    }

    [Fact]
    public async Task Own_request_replaces_the_empty_state_with_its_card()
    {
        // Arrange
        await BuildAsync();
        var packs = Page().Packs;

        // Act
        Requests.Ask(Game, "ru", null);

        // Assert
        (packs.ShowRequestCard, packs.ShowNoPacks, packs.Request!.CardTitle).Should().Be((true, false, "Requested: Русский"));
    }

    [Fact]
    public async Task Card_says_who_is_translating()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 5, new RegistryRequestClaim("alice", RequestClaimState.Working, GenerationStage.Translate, 45, DateTimeOffset.UtcNow)));
        Store.Ask(Key, FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);
        Services.GetRequiredService<FakeGitHubHost>().Account = null;

        // Act
        var request = Page().Packs.Request!;

        // Assert
        (request.ClaimText, request.Steps.Select(s => s.IsDone)).Should().BeEquivalentTo(("alice is translating it: Translate · 45%", new[] { true, true, false, false }));
    }

    [Fact]
    public async Task Failed_ask_shows_the_reason()
    {
        // Arrange
        await BuildAsync();
        Discussions.Error = new RatingError(RatingProblem.Network, "offline");
        var packs = Page().Packs;

        // Act
        Requests.Ask(Game, "ru", null);

        // Assert
        (packs.Request!.IsFailed, packs.Request.StatusText).Should().Be((true, RatingTexts.Error(Discussions.Error, Services.GetRequiredService<Localization.Localizer>())));
    }

    [Fact]
    public async Task Withdraw_forgets_the_request()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 3));
        Requests.Ask(Game, "ru", null);

        // Act
        Requests.Withdraw(Alpha, "ru");

        // Assert
        (Store.Find(Key), Discussions.Withdrawals.Count).Should().Be((null, 1));
    }

    [Fact]
    public async Task Done_request_shows_the_strip_over_the_list()
    {
        // Arrange
        await BuildAsync();
        Store.Ask(Key, FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);
        _registry.Pack(10, "ru", "full").Write();
        await Services.GetRequiredService<PackIndex>().RefreshAsync();

        // Act
        var packs = Page().Packs;

        // Assert
        packs.ShowRequestDone.Should().BeTrue();
    }

    [Fact]
    public async Task Dismissed_strip_stays_away()
    {
        // Arrange
        await BuildAsync();
        Store.Ask(Key, FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);
        _registry.Pack(10, "ru", "full").Write();
        await Services.GetRequiredService<PackIndex>().RefreshAsync();
        var page = Page();

        // Act
        page.Packs.Request!.DismissDoneCommand.Execute(null);

        // Assert
        page.Packs.ShowRequestDone.Should().BeFalse();
    }

    // ---------- Activity ----------

    [Fact]
    public async Task Pack_for_the_players_request_is_told_once()
    {
        // Arrange
        await BuildAsync();
        Store.Ask(Key, FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);
        _registry.Pack(10, "ru", "full").Write();
        var index = Services.GetRequiredService<PackIndex>();

        // Act
        await index.RefreshAsync();
        await index.RefreshAsync();

        // Assert
        Services.GetRequiredService<ActivityStore>().Feed.Where(e => e.Kind == ActivityEventKind.RequestDone).Should().ContainSingle()
            .Which.PackTitle.Should().Be("full");
    }

    [Fact]
    public async Task Translator_taking_the_players_request_is_told()
    {
        // Arrange
        await BuildAsync();
        Store.Ask(Key, FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);
        _registry.Request(10, "ru", 4, new RegistryRequestClaim("alice", RequestClaimState.Working, GenerationStage.Glossary, 20, DateTimeOffset.UtcNow)).Write();

        // Act
        await Services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        Services.GetRequiredService<ActivityStore>().Feed.Single(e => e.Kind == ActivityEventKind.RequestTaken).Detail.Should().Be("alice");
    }

    // ---------- Create with AI ----------

    [Fact]
    public async Task Start_form_names_the_request()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 13));

        // Act
        var generation = Page(GamePageTab.Generation).Generation;

        // Assert
        (generation.ShowRequest, generation.TakeRequest, generation.TakeRequestText).Should().Be(
            (true, true, "Tell the 13 players who asked that I'm translating (posted on GitHub under my name)"));
    }

    [Fact]
    public async Task Someone_elses_claim_leaves_the_checkbox_off()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 13, new RegistryRequestClaim("alice", RequestClaimState.Working, GenerationStage.Translate, 45, DateTimeOffset.UtcNow)));
        Services.GetRequiredService<FakeGitHubHost>().Account = null;

        // Act
        var generation = Page(GamePageTab.Generation).Generation;

        // Assert
        (generation.TakeRequest, generation.RequestOtherClaimText).Should().Be((false, "alice is already translating it (45%); you can still make your own."));
    }

    [Fact]
    public async Task Without_a_request_the_form_has_no_strip()
    {
        // Arrange
        await BuildAsync();

        // Act
        var generation = Page(GamePageTab.Generation).Generation;

        // Assert
        generation.ShowRequest.Should().BeFalse();
    }

    [Fact]
    public async Task Taking_a_request_posts_the_claim()
    {
        // Arrange
        await BuildAsync();

        // Act
        Requests.Take(Job(stage: GenerationStage.Setup), FakeRequestDiscussions.Url);

        // Assert
        (Discussions.Claims.Single().Claim.State, Store.FindClaim("job1")!.Comment).Should().Be((RequestClaimState.Working, "DC_claim"));
    }

    [Fact]
    public async Task Taking_a_request_signed_out_asks_to_sign_in()
    {
        // Arrange
        await BuildAsync(signedIn: false);

        // Act
        Requests.Take(Job(), FakeRequestDiscussions.Url);

        // Assert
        (Services.GetRequiredService<IDialogService>().Current is RatingSignInDialogViewModel, Discussions.Claims.Count).Should().Be((true, 0));
    }

    [Fact]
    public void Completed_job_claims_built()
    {
        // Act
        var claim = TranslationRequests.ClaimOf(Job(GenerationJobStatus.Completed, GenerationStage.Done));

        // Assert
        (claim.State, claim.Percent).Should().Be((RequestClaimState.Built, 100));
    }

    [Fact]
    public void Cancelled_job_stops_the_claim()
    {
        // Act
        var claim = TranslationRequests.ClaimOf(Job(GenerationJobStatus.Cancelled));

        // Assert
        claim.State.Should().Be(RequestClaimState.Stopped);
    }

    [Theory]
    [InlineData(GenerationStage.Translate, 30, false)]
    [InlineData(GenerationStage.Translate, 50, true)]
    [InlineData(GenerationStage.Build, 26, true)]
    public void Claim_is_sent_again_on_a_new_stage_or_a_percent_step(GenerationStage stage, int percent, bool news)
    {
        // Arrange
        var sent = new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Translate, 26);

        // Act
        var result = TranslationRequests.IsNews(sent, sent with { Stage = stage, Percent = percent });

        // Assert
        result.Should().Be(news);
    }

    // ---------- Library ----------

    [Fact]
    public async Task Library_card_shows_the_request()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 31));

        // Act
        var library = Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

        // Assert
        library.Cards.Single(c => c.Name == "Alpha").BadgeText.Should().Be("Requested · 31");
    }

    [Fact]
    public async Task Library_card_shows_the_players_own_request()
    {
        // Arrange
        await BuildAsync();
        Store.Ask(Key, FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);

        // Act
        var library = Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

        // Assert
        library.Cards.Single(c => c.Name == "Alpha").BadgeText.Should().Be("You asked · 1");
    }

    [Fact]
    public async Task Requested_filter_keeps_the_requested_games()
    {
        // Arrange
        await BuildAsync(r => r.Request(10, "ru", 31));
        var library = Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

        // Act
        library.SelectedFilter = LibraryFilter.Requested;

        // Assert
        library.VisibleCards.Select(c => c.Name).Should().Equal("Alpha");
    }
}
