using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.GamePage;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The primary action's label and caption for every state (design §3).</summary>
public sealed class PrimaryActionTextsTests
{
    // The culture's short time as ICU writes it (a narrow no-break space before "PM" on newer ICU).
    private static readonly string SixPm = new DateTime(2026, 11, 9, 18, 0, 0).ToString("t", CultureInfo.GetCultureInfo("en"));

    private static Localizer L => Localizer.Current;

    private static RegistryPack Pack(string title = "Фонарщики", PackStatus status = PackStatus.Verified, string build = "1000", int revision = 4) =>
        new(
            "lanterns",
            GameId.Steam(10),
            "ru",
            title,
            new PackAuthor("alice"),
            status,
            revision,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new PackCoverage(10, 10),
            [build],
            1,
            "games/steam-10/ru/lanterns/manifest.json",
            100,
            new RegistryRating(0, 0, 0));

    private static (string Label, string Caption) Texts(GamePrimaryAction action, Game? game = null) =>
        (PrimaryActionTexts.Label(action, L), PrimaryActionTexts.Caption(action, game ?? TestGames.Steam(10, "Lantern"), L));

    [Fact]
    public void Install_names_the_pack_its_status_and_that_it_fits_the_build()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.InstallBestPack, Pack());

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Install best pack", "«Фонарщики» · verified · made for your version"));
    }

    [Fact]
    public void Install_of_a_pack_for_another_build_leaves_the_version_out()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.InstallBestPack, Pack(status: PackStatus.Alpha, build: "900"));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Install best pack", "«Фонарщики» · alpha"));
    }

    [Fact]
    public void Generate_names_the_agent_subscription()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.Generate);

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Generate translation with AI", "Uses your Claude Code or Codex subscription"));
    }

    [Fact]
    public void Play_of_a_steam_game_goes_through_steam()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.Play, Pack(), new InstalledPackState("lanterns", InstalledPackProblem.None, 4));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Play", "via Steam · with «Фонарщики»"));
    }

    [Fact]
    public void Play_of_a_manual_game_names_only_the_pack()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.Play, null, new InstalledPackState("lanterns", InstalledPackProblem.None, 4));

        // Act
        var texts = Texts(action, TestGames.Manual("Lantern"));

        // Assert
        texts.Should().Be(("Play", "with «lanterns»"));
    }

    [Fact]
    public void Update_names_both_revisions()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.UpdatePack, Pack(revision: 4), new InstalledPackState("lanterns", InstalledPackProblem.Incompatible, 3));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Update to rev 4", "Replaces the installed rev 3"));
    }

    [Fact]
    public void Translate_new_strings_says_no_revision_exists()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.TranslateNewStrings, Pack(), new InstalledPackState("lanterns", InstalledPackProblem.Incompatible, 4));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Translate new strings", "No revision for the new game version yet"));
    }

    [Fact]
    public void View_progress_shows_percent_and_time_left()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.ViewProgress, Job: new GenerationJobState(42, GenerationJobPhase.Running, TimeSpan.FromMinutes(24.2)));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("View progress", "Generating · 42% · ≈ 25 min left"));
    }

    [Fact]
    public void Time_left_over_an_hour_is_in_hours_and_minutes()
    {
        // Arrange
        var action = new GamePrimaryAction(GamePrimaryActionKind.ViewProgress, Job: new GenerationJobState(42, GenerationJobPhase.Running, TimeSpan.FromMinutes(130)));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Caption.Should().Be("Generating · 42% · ≈ 2 h 10 min left");
    }

    [Fact]
    public void Job_paused_by_the_limit_offers_resume_with_the_reset_time()
    {
        // Arrange
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var job = new GenerationJobState(42, GenerationJobPhase.PausedByLimit, ResumeAt: new DateTimeOffset(2026, 11, 9, 18, 0, 0, TimeSpan.Zero));
        var action = new GamePrimaryAction(GamePrimaryActionKind.ResumeGeneration, Job: job);

        // Act
        var texts = (PrimaryActionTexts.Label(action, L), PrimaryActionTexts.Caption(action, TestGames.Steam(10, "Lantern"), L, clock));

        // Assert
        texts.Should().Be(("Resume", "Paused: subscription limit · resumes ~" + SixPm));
    }

    [Theory]
    [InlineData(GenerationJobPhase.PausedByPlayer, GamePrimaryActionKind.ResumeGeneration, "Resume", "Paused · 42%")]
    [InlineData(GenerationJobPhase.Interrupted, GamePrimaryActionKind.ResumeGeneration, "Resume", "Interrupted at 42%")]
    [InlineData(GenerationJobPhase.WaitingForReview, GamePrimaryActionKind.ReviewGlossary, "Review glossary", "Waiting for you")]
    public void Stopped_job_names_its_state(GenerationJobPhase phase, GamePrimaryActionKind kind, string label, string caption)
    {
        // Arrange
        var action = new GamePrimaryAction(kind, Job: new GenerationJobState(42, phase));

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be((label, caption));
    }

    [Fact]
    public void Failed_job_offers_retry_naming_the_stage()
    {
        // Arrange
        var job = new GenerationJobState(90, GenerationJobPhase.Failed, FailedStage: AiLocalPack.Core.Generation.GenerationStage.Build);
        var action = new GamePrimaryAction(GamePrimaryActionKind.RetryStage, Job: job);

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be(("Retry stage", "Stage failed: Build & test"));
    }

    [Fact]
    public void No_action_has_no_texts()
    {
        // Arrange
        var action = GamePrimaryAction.None;

        // Act
        var texts = Texts(action);

        // Assert
        texts.Should().Be((string.Empty, string.Empty));
    }
}
