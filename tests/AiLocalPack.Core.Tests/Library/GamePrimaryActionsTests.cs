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
        var installed = new InstalledPackState("mine", false, 4);
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
        var installed = new InstalledPackState("mine", true, 3);
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
        var installed = new InstalledPackState("mine", true, 4);
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
        var status = Status(LocalizationStatusKind.NeedsUpdate, [], new InstalledPackState("mine", true, 2));

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Kind.Should().Be(GamePrimaryActionKind.TranslateNewStrings);
    }

    [Theory]
    [InlineData(LocalizationStatusKind.Generating, false)]
    [InlineData(LocalizationStatusKind.Paused, true)]
    public void Generation_job_offers_its_progress(LocalizationStatusKind kind, bool paused)
    {
        // Arrange
        var job = new GenerationJobState(42, paused);
        var status = Status(kind, [Pack("best")], job: job);

        // Act
        var action = GamePrimaryActions.Resolve(status);

        // Assert
        action.Should().Be(new GamePrimaryAction(GamePrimaryActionKind.ViewProgress, Job: job));
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
            ? new GenerationJobState(10, kind == LocalizationStatusKind.Paused)
            : null;
        var status = Status(kind, job: job);

        // Act
        var opensOnGeneration = GamePrimaryActions.OpensOnGeneration(status);

        // Assert
        opensOnGeneration.Should().Be(expected);
    }
}
