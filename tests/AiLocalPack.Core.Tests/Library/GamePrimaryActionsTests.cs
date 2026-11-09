using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Library;

public sealed class GamePrimaryActionsTests
{
    private static readonly GameId Id = GameId.Steam(620);

    private static RegistryPack Pack(string id, int revision = 1) =>
        new(
            id,
            Id,
            "ru",
            id,
            new PackAuthor("alice"),
            PackStatus.Verified,
            revision,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new PackCoverage(10, 10),
            ["1000"],
            1,
            $"games/steam-620/ru/{id}/manifest.json",
            100,
            new RegistryRating(0, 0, 0));

    private static LocalizationStatus Status(
        LocalizationStatusKind kind,
        IReadOnlyList<RegistryPack>? packs = null,
        InstalledPackState? installed = null,
        GenerationJobState? job = null,
        LocalizationBlock block = LocalizationBlock.None) =>
        new(kind, block, packs ?? [], Installed: installed, Job: job);

    [Fact]
    public void Pack_available_installs_the_best_pack()
    {
        // Arrange
        var best = Pack("best");
        var status = Status(LocalizationStatusKind.PackAvailable, [best, Pack("other")]);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(GamePrimaryActionKind.InstallBestPack, best));
    }

    [Fact]
    public void No_pack_offers_generation()
    {
        // Arrange
        var status = Status(LocalizationStatusKind.NoPack);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.Generate);
    }

    [Fact]
    public void Installed_pack_offers_play_with_that_pack()
    {
        // Arrange
        var installedPack = Pack("mine", revision: 4);
        var installed = new InstalledPackState("mine", InstalledPackProblem.None, 4);
        var status = Status(LocalizationStatusKind.Installed, [Pack("best"), installedPack], installed);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(GamePrimaryActionKind.Play, installedPack, installed));
    }

    [Fact]
    public void Broken_pack_with_a_later_revision_offers_the_update()
    {
        // Arrange
        var newer = Pack("mine", revision: 4);
        var installed = new InstalledPackState("mine", InstalledPackProblem.Incompatible, 3);
        var status = Status(LocalizationStatusKind.NeedsUpdate, [newer], installed);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(GamePrimaryActionKind.UpdatePack, newer, installed));
    }

    [Fact]
    public void Broken_pack_without_a_later_revision_offers_translating_the_new_strings()
    {
        // Arrange
        var installed = new InstalledPackState("mine", InstalledPackProblem.Incompatible, 4);
        var status = Status(LocalizationStatusKind.NeedsUpdate, [Pack("mine", revision: 4)], installed);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.TranslateNewStrings);
    }

    [Fact]
    public void Broken_pack_gone_from_the_registry_offers_translating_the_new_strings()
    {
        // Arrange
        var status = Status(LocalizationStatusKind.NeedsUpdate, [], new InstalledPackState("mine", InstalledPackProblem.Incompatible, 2));

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.TranslateNewStrings);
    }

    [Theory]
    [InlineData(InstalledPackProblem.Wiped)]
    [InlineData(InstalledPackProblem.ReapplyFailed)]
    public void Pack_that_still_fits_offers_reapply(InstalledPackProblem problem)
    {
        // Arrange
        var installed = new InstalledPackState("mine", problem, 4);
        var status = Status(LocalizationStatusKind.NeedsUpdate, [Pack("mine", revision: 4)], installed);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.Reapply);
    }

    [Fact]
    public void Pack_that_still_fits_with_a_later_revision_offers_the_update()
    {
        // Arrange
        var installed = new InstalledPackState("mine", InstalledPackProblem.Wiped, 3);
        var status = Status(LocalizationStatusKind.NeedsUpdate, [Pack("mine", revision: 4)], installed);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.UpdatePack);
    }

    [Theory]
    [InlineData(GenerationJobPhase.Running, GamePrimaryActionKind.ViewProgress)]
    [InlineData(GenerationJobPhase.PausedByLimit, GamePrimaryActionKind.ResumeGeneration)]
    [InlineData(GenerationJobPhase.PausedByPlayer, GamePrimaryActionKind.ResumeGeneration)]
    [InlineData(GenerationJobPhase.Interrupted, GamePrimaryActionKind.ResumeGeneration)]
    [InlineData(GenerationJobPhase.Failed, GamePrimaryActionKind.RetryStage)]
    [InlineData(GenerationJobPhase.WaitingForReview, GamePrimaryActionKind.ReviewGlossary)]
    public void Generation_job_offers_its_next_step(GenerationJobPhase phase, GamePrimaryActionKind expected)
    {
        // Arrange
        var job = new GenerationJobState(42, phase);
        var kind = phase == GenerationJobPhase.Running ? LocalizationStatusKind.Generating : LocalizationStatusKind.Paused;
        var status = Status(kind, [Pack("best")], job: job);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(expected, Job: job));
    }

    [Theory]
    [InlineData(LocalizationBlock.AntiCheat)]
    [InlineData(LocalizationBlock.OptOut)]
    public void Blocked_game_has_no_action(LocalizationBlock block)
    {
        // Arrange
        var status = Status(LocalizationStatusKind.Blocked, [Pack("best")], block: block);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(GamePrimaryAction.None);
    }

    [Fact]
    public void Unknown_status_has_no_action()
    {
        // Arrange
        var status = LocalizationStatus.Unknown;

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(GamePrimaryAction.None);
    }

    [Theory]
    [InlineData(LocalizationStatusKind.Generating, true)]
    [InlineData(LocalizationStatusKind.Paused, true)]
    [InlineData(LocalizationStatusKind.PackAvailable, false)]
    [InlineData(LocalizationStatusKind.NoPack, false)]
    public void Page_opens_on_generation_only_while_a_job_exists(LocalizationStatusKind kind, bool expected)
    {
        // Arrange
        var job = kind is LocalizationStatusKind.Generating or LocalizationStatusKind.Paused
            ? new GenerationJobState(10, kind == LocalizationStatusKind.Paused ? GenerationJobPhase.PausedByPlayer : GenerationJobPhase.Running)
            : null;
        var status = Status(kind, job: job);

        // Act
        var opensOnGeneration = GamePrimaryActions.OpensOnGeneration(status);

        // Assert
        opensOnGeneration.Should().Be(expected);
    }

    private static GenerationResultState Result(bool tried = false) => new("claude-code", "ru", "Claude Code translation", 0.99, true, tried);

    [Fact]
    public void Waiting_local_pack_offers_install_and_play()
    {
        // Arrange
        var status = Status(LocalizationStatusKind.PackAvailable, [Pack("best")]) with { Result = Result() };

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(GamePrimaryActionKind.InstallLocalPack, Result: Result()));
    }

    [Fact]
    public void Waiting_local_pack_yields_to_the_installed_pack()
    {
        // Arrange
        var installed = new InstalledPackState("best", InstalledPackProblem.None, 1);
        var status = Status(LocalizationStatusKind.Installed, [Pack("best")], installed) with { Result = Result() };

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.Play);
    }

    [Fact]
    public void Installed_local_pack_plays_without_the_registry_pack_of_the_same_id()
    {
        // Arrange
        var installed = new InstalledPackState("claude-code", InstalledPackProblem.None, 1, Local: true, Title: "Claude Code translation");
        var status = Status(LocalizationStatusKind.Installed, [Pack("claude-code")], installed);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(GamePrimaryActionKind.Play, null, installed));
    }

    [Fact]
    public void Untried_result_opens_the_page_on_generation()
    {
        // Arrange
        var status = Status(LocalizationStatusKind.NoPack) with { Result = Result() };

        // Act
        var opens = GamePrimaryActions.OpensOnGeneration(status);

        // Assert
        opens.Should().BeTrue();
    }

    [Fact]
    public void Tried_result_opens_the_page_on_packs()
    {
        // Arrange
        var status = Status(LocalizationStatusKind.NoPack) with { Result = Result(tried: true) };

        // Act
        var opens = GamePrimaryActions.OpensOnGeneration(status);

        // Assert
        opens.Should().BeFalse();
    }

    [Fact]
    public void Result_installed_in_another_language_offers_play()
    {
        // Arrange: the page's target is another language, so the status has no installed pack.
        var status = Status(LocalizationStatusKind.NoPack) with { Result = Result() with { Installed = true } };

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.Play);
    }
}
