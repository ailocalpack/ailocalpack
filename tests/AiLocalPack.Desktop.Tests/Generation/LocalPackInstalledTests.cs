using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>Whether an install record is a local pack (tasks 55, 56: an update's next revision is another pack until installed).</summary>
public sealed class LocalPackInstalledTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static LocalPack Pack(int revision) => new(
        "/local/lanterns",
        new PackManifest(
            "lanterns",
            GameId.Steam(10),
            "ru",
            "Фонарщики",
            [new PackAuthor(LocalPackStore.PlaceholderAuthor)],
            [.. Enumerable.Range(1, revision).Select(n => new PackRevision(n, T0, LocalPackStore.PlaceholderAuthor))],
            PackStatus.Alpha,
            null,
            [],
            new PackRecipe("rpgmaker", "1.0.0"),
            null,
            null,
            [],
            [],
            new PackCoverage(1, 1),
            T0,
            T0),
        new LocalPackReport("job", T0, TestRunResult.Skipped(TestRunSkip.TurnedOff, "off"), LocalPackFonts.NotChecked, [], [], 0));

    private static InstalledPack Record(int revision) =>
        new(GameId.Steam(10), "ru", "lanterns", revision, T0, "/games/alpha", "backup", [], []) { Local = true };

    [Fact]
    public void The_installed_revision_is_installed()
    {
        // Act
        var installed = LocalPacks.IsInstalled(Record(3), Pack(3));

        // Assert
        installed.Should().BeTrue();
    }

    [Fact]
    public void The_next_revision_of_the_installed_pack_is_not()
    {
        // Act
        var installed = LocalPacks.IsInstalled(Record(3), Pack(4));

        // Assert
        installed.Should().BeFalse();
    }
}
