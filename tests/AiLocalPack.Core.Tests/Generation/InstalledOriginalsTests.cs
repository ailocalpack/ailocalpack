using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Tests.Install;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>
/// Generation reads the game without the pack installed in its folder (task 56, _Live game folder_): the pack's files
/// come from its backup, the files it added are left out, a file the game changed since stays the game's.
/// </summary>
public sealed class InstalledOriginalsTests : IDisposable
{
    private readonly InstallFixture _fx = new();

    public InstalledOriginalsTests()
    {
        _fx.Installer().Install(
            _fx.Request(("data/Map001.json", "map 1 RU"), ("data/System.json", "system RU"), ("data/ru.json", "added by the pack")),
            null,
            cancellationToken: Ct);
    }

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_pack_files_read_from_the_backup_and_its_added_files_are_left_out()
    {
        // Act
        var originals = InstalledOriginals.Find(_fx.Paths, _fx.GameFolder.Path);

        // Assert
        new
        {
            Restored = originals.Restored.Keys.Order(StringComparer.Ordinal).ToList(),
            System = File.ReadAllText(originals.Restored["data/System.json"]),
            originals.Added,
        }.Should().BeEquivalentTo(new
        {
            Restored = new[] { "data/Map001.json", "data/System.json" },
            System = "system original",
            Added = new HashSet<string> { "data/ru.json" },
        });
    }

    [Fact]
    public void A_file_the_game_rewrote_after_the_install_stays_the_games()
    {
        // Arrange
        File.WriteAllText(_fx.GameFolder.Combine("data/Map001.json"), "map 1 updated");

        // Act
        var originals = InstalledOriginals.Find(_fx.Paths, _fx.GameFolder.Path);

        // Assert
        originals.Restored.Should().NotContainKey("data/Map001.json");
    }

    [Fact]
    public void A_game_without_a_pack_has_no_originals()
    {
        // Arrange
        using var other = new TempDir();
        other.File("data/System.json", "other");

        // Act
        var originals = InstalledOriginals.Find(_fx.Paths, other.Path);

        // Assert
        originals.IsEmpty.Should().BeTrue();
    }
}
