using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Covers;

namespace AiLocalPack.Desktop.Tests.Covers;

public sealed class ManualCoverStoreTests : IDisposable
{
    private static readonly GameId Id = GameId.Fingerprint(new string('b', GameId.FingerprintLength));
    private static readonly GameId Other = GameId.Fingerprint(new string('c', GameId.FingerprintLength));

    private readonly TempDir _home = new();
    private readonly AppPaths _paths;
    private readonly ManualCoverStore _store;

    public ManualCoverStoreTests()
    {
        _paths = AppPaths.UnderHome(_home.Path);
        _store = new ManualCoverStore(_paths, new TestLogger<ManualCoverStore>());
    }

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Saved_icon_is_found_as_an_icon()
    {
        // Arrange
        _store.Save(Id, [1, 2, 3], ".png", isIcon: true);

        // Act
        var cover = _store.Find(Id);

        // Assert
        cover.Should().Be(new StoredCover(Path.Combine(_paths.GameCovers, $"fp-{new string('b', 64)}.icon.png"), true));
        File.ReadAllBytes(cover!.Path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Picked_image_replaces_the_icon()
    {
        // Arrange
        _store.Save(Id, [1], ".ico", isIcon: true);

        // Act
        _store.Save(Id, [2], ".JPG", isIcon: false);

        // Assert
        _store.Find(Id).Should().Be(new StoredCover(Path.Combine(_paths.GameCovers, $"fp-{new string('b', 64)}.cover.jpg"), false));
        Directory.GetFiles(_paths.GameCovers).Should().ContainSingle();
    }

    [Fact]
    public void Game_without_a_cover_has_none()
    {
        // Arrange
        _store.Save(Other, [1], ".png", isIcon: false);

        // Act
        var cover = _store.Find(Id);

        // Assert
        cover.Should().BeNull();
    }

    [Fact]
    public void Delete_removes_only_that_games_cover()
    {
        // Arrange
        _store.Save(Id, [1], ".png", isIcon: false);
        _store.Save(Other, [2], ".png", isIcon: false);

        // Act
        _store.Delete(Id);

        // Assert
        _store.Find(Id).Should().BeNull();
        _store.Find(Other).Should().NotBeNull();
    }

    [Fact]
    public void Extension_that_is_not_a_plain_name_is_rejected()
    {
        // Arrange
        var bad = "./../x";

        // Act
        var act = () => _store.Save(Id, [1], bad, isIcon: false);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unwritable_folder_is_logged_not_thrown()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.GameCovers, "a file where the folder should be");

        // Act
        var saved = _store.Save(Id, [1], ".png", isIcon: false);

        // Assert
        saved.Should().BeFalse();
    }
}
