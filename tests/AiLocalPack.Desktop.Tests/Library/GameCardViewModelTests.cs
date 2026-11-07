using System.Globalization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.Library;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Tests.Library;

public sealed class GameCardViewModelTests
{
    private static GameCardViewModel Card(Game game)
    {
        var card = new GameCardViewModel(game, new Localizer(CultureInfo.GetCultureInfo("en")), _ => { }, _ => { });
        card.ApplyStatus(LocalizationStatuses.Resolve(game, "ru", AntiCheatPolicy.Bundled.Resolve(game), null), stale: false);
        return card;
    }

    [Theory]
    [InlineData("unity:il2cpp", "Unity (IL2CPP)")]
    [InlineData("unreal:5", "Unreal Engine 5")]
    [InlineData("unknown", "Unknown engine")]
    [InlineData(null, "")]
    public void Context_line_shows_the_engine(string? engine, string expected)
    {
        // Arrange
        var card = Card(TestGames.Steam(10, "Alpha", engine: engine));

        // Act
        var line = card.ContextLine;

        // Assert
        line.Should().Be(expected);
    }

    [Fact]
    public void Anti_cheat_game_is_blocked_with_its_name()
    {
        // Arrange
        var game = TestGames.Steam(10, "Arena", engine: "unreal:5", antiCheat: "eac");

        // Act
        var card = Card(game);

        // Assert
        card.IsBlocked.Should().BeTrue();
        card.ContextLine.Should().Be("Easy Anti-Cheat");
        card.BadgeText.Should().Be("Anti-cheat");
        card.AccessibleName.Should().Be("Arena (blocked: Easy Anti-Cheat)");
    }

    [Fact]
    public void Game_without_anti_cheat_is_not_blocked()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha", antiCheat: AntiCheatPolicy.NoneId);

        // Act
        var card = Card(game);

        // Assert
        card.IsBlocked.Should().BeFalse();
        card.AccessibleName.Should().Be("Alpha");
    }

    [Fact]
    public void Unavailable_game_is_dimmed_and_cannot_open_its_folder()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha", engine: "renpy", available: false);

        // Act
        var card = Card(game);

        // Assert
        card.IsDimmed.Should().BeTrue();
        card.ContextLine.Should().Be("Folder not available");
        card.OpenFolderCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Manual_game_has_the_manual_marker()
    {
        // Arrange
        var game = TestGames.Manual("Indie", engine: "renpy");

        // Act
        var card = Card(game);

        // Assert
        card.IsManual.Should().BeTrue();
        card.ManualText.Should().Be("manual");
        card.ShowManualSeparator.Should().BeTrue();
    }

    [Fact]
    public void Steam_game_has_no_manual_marker()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha", engine: "renpy");

        // Act
        var card = Card(game);

        // Assert
        card.IsManual.Should().BeFalse();
        card.ShowManualSeparator.Should().BeFalse();
    }

    // On the headless UI thread: brushes are Avalonia objects (see GameCardViewModel.PlaceholderBrush).
    [Fact]
    public Task Placeholder_color_is_the_same_for_the_same_game() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha");

        // Act
        var (first, second) = (Card(game).PlaceholderBrush, Card(game).PlaceholderBrush);

        // Assert
        Colors(first).Should().Equal(Colors(second));
    });

    private static IEnumerable<Color> Colors(IBrush brush) =>
        brush.Should().BeAssignableTo<IGradientBrush>().Which.GradientStops.Select(s => s.Color).ToList();

    [Fact]
    public void Update_with_another_game_is_rejected()
    {
        // Arrange
        var card = Card(TestGames.Steam(10, "Alpha"));

        // Act
        var act = () => card.Update(TestGames.Steam(20, "Beta"));

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Update_raises_the_changed_texts()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha");
        var card = Card(game);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        card.Update(game with { Engine = "renpy" });

        // Assert
        changed.Should().Contain(nameof(GameCardViewModel.ContextLine));
        card.ContextLine.Should().Be("Ren'Py");
    }
}

/// <summary>Badge, context line and quick action of each status (task 21).</summary>
public sealed class GameCardStatusTests
{
    private static readonly Game Alpha = TestGames.Steam(10, "Alpha", engine: "renpy");

    private static RegistryPack Pack(string id, PackStatus status = PackStatus.Alpha, int likes = 0) =>
        new(id, Alpha.Id, "ru", id, new PackAuthor("alice"), status, 1, DateTimeOffset.UnixEpoch, new PackCoverage(1, 1), ["1000"], 1, "m/manifest.json", 1, new RegistryRating(likes, 0, 0));

    private static GameCardViewModel Card(LocalizationStatus status, Game? game = null, bool stale = false)
    {
        var card = new GameCardViewModel(game ?? Alpha, new Localizer(CultureInfo.GetCultureInfo("en")), _ => { }, _ => { });
        card.ApplyStatus(status, stale);
        return card;
    }

    private static LocalizationStatus Status(LocalizationStatusKind kind, params RegistryPack[] packs) => new(kind, LocalizationBlock.None, packs);

    [Theory]
    [InlineData(LocalizationStatusKind.Installed, "Installed", "")]
    [InlineData(LocalizationStatusKind.PackAvailable, "Pack available", "Install")]
    [InlineData(LocalizationStatusKind.NoPack, "No pack", "Generate with AI")]
    [InlineData(LocalizationStatusKind.NeedsUpdate, "Needs update", "Update")]
    public void Badge_and_quick_action_follow_the_status(LocalizationStatusKind kind, string badge, string action)
    {
        // Act
        var card = Card(Status(kind));

        // Assert
        (card.BadgeText, card.QuickActionText, card.HasQuickAction).Should().Be((badge, action, action.Length > 0));
    }

    [Fact]
    public void Pack_that_only_needs_reapplying_offers_reapply()
    {
        // Act
        var card = Card(new LocalizationStatus(LocalizationStatusKind.NeedsUpdate, LocalizationBlock.None, [], Installed: new InstalledPackState("mine", InstalledPackProblem.Wiped)));

        // Assert
        (card.BadgeText, card.QuickActionText).Should().Be(("Needs update", "Re-apply"));
    }

    [Fact]
    public void Generating_shows_its_percent_a_progress_bar_and_view()
    {
        // Act
        var card = Card(new LocalizationStatus(LocalizationStatusKind.Generating, LocalizationBlock.None, [], Job: new GenerationJobState(42, false, TimeSpan.FromMinutes(24.5))));

        // Assert
        (card.BadgeText, card.ShowProgress, card.Percent, card.QuickActionText, card.ContextLine)
            .Should().Be(("Generating · 42%", true, 42, "View", "≈ 25 min"));
    }

    [Fact]
    public void Paused_shows_its_reason_and_resume()
    {
        // Act
        var card = Card(new LocalizationStatus(LocalizationStatusKind.Paused, LocalizationBlock.None, [], Job: new GenerationJobState(68, true, PauseReason: "subscription limit")));

        // Assert
        (card.BadgeText, card.IsPaused, card.QuickActionText, card.ContextLine)
            .Should().Be(("Paused · 68%", true, "Resume", "subscription limit"));
    }

    [Fact]
    public void Blocked_has_no_quick_action()
    {
        // Act
        var card = Card(new LocalizationStatus(LocalizationStatusKind.Blocked, LocalizationBlock.AntiCheat, [], "Easy Anti-Cheat"));

        // Assert
        (card.HasQuickAction, card.QuickActionCommand.CanExecute(null)).Should().Be((false, false));
    }

    [Fact]
    public void Unknown_status_has_no_badge_and_shows_the_engine()
    {
        // Act
        var card = Card(LocalizationStatus.Unknown);

        // Assert
        (card.ShowBadge, card.HasQuickAction, card.ContextLine, card.AccessibleName).Should().Be((false, false, "Ren'Py", "Alpha"));
    }

    [Fact]
    public void No_pack_shows_the_engine()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.NoPack));

        // Assert
        card.ContextLine.Should().Be("Ren'Py");
    }

    [Fact]
    public void One_pack_shows_its_status()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.PackAvailable, Pack("full", PackStatus.Verified)));

        // Assert
        card.ContextLine.Should().Be("verified");
    }

    [Fact]
    public void One_alpha_pack_shows_alpha()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.PackAvailable, Pack("full")));

        // Assert
        card.ContextLine.Should().Be("alpha");
    }

    [Fact]
    public void Several_packs_show_the_count()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.PackAvailable, Pack("a"), Pack("b"), Pack("c")));

        // Assert
        card.ContextLine.Should().Be("3 packs");
    }

    [Fact]
    public void Likes_of_the_best_pack_are_shown()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.Installed, Pack("full", likes: 1234)));

        // Assert
        (card.ShowLikes, card.LikesText, card.ShowLikesSeparator).Should().Be((true, "1,234", true));
    }

    [Fact]
    public void Pack_without_likes_shows_no_likes()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.PackAvailable, Pack("full")));

        // Assert
        card.ShowLikes.Should().BeFalse();
    }

    [Fact]
    public void Status_from_the_saved_list_is_faded()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.NoPack), stale: true);

        // Assert
        card.IsStale.Should().BeTrue();
    }

    [Fact]
    public void Screen_reader_name_includes_the_status()
    {
        // Act
        var card = Card(Status(LocalizationStatusKind.PackAvailable, Pack("full")));

        // Assert
        card.AccessibleName.Should().Be("Alpha: Pack available");
    }

    [Fact]
    public void Opted_out_game_shows_unavailable()
    {
        // Act
        var card = Card(new LocalizationStatus(LocalizationStatusKind.Blocked, LocalizationBlock.OptOut, []));

        // Assert
        (card.BadgeText, card.ContextLine, card.IsBlocked).Should().Be(("Unavailable", "Studio opted out", true));
    }

    [Fact]
    public void Quick_action_runs_the_given_action()
    {
        // Arrange
        GameCardViewModel? ran = null;
        var card = new GameCardViewModel(Alpha, new Localizer(CultureInfo.GetCultureInfo("en")), _ => { }, _ => { }, quickAction: c => ran = c);
        card.ApplyStatus(Status(LocalizationStatusKind.NoPack), stale: false);

        // Act
        card.QuickActionCommand.Execute(null);

        // Assert
        ran.Should().BeSameAs(card);
    }

    [Fact]
    public void New_status_raises_the_badge_text()
    {
        // Arrange
        var card = Card(Status(LocalizationStatusKind.NoPack));
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        card.ApplyStatus(Status(LocalizationStatusKind.PackAvailable, Pack("full")), stale: false);

        // Assert
        (changed.Count > 0, card.BadgeText).Should().Be((true, "Pack available"));
    }
}
