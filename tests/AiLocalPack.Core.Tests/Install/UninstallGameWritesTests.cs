using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>An uninstall undoes what the game wrote since the install (caches, settings), never the player's saves (_Live game folder_).</summary>
public sealed class UninstallGameWritesTests : IDisposable
{
    private readonly InstallFixture _fx = new();

    public UninstallGameWritesTests()
    {
        _fx.GameFolder.File("settings.ini", "language=en");
    }

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PackInstaller Install()
    {
        var installer = _fx.Installer();
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        return installer;
    }

    [Fact]
    public void A_cache_the_game_built_from_the_translation_is_deleted()
    {
        // Arrange
        var installer = Install();
        _fx.GameFolder.File("books/compiled_books.json", "compiled from the translation");

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Exists("books/compiled_books.json").Should().BeFalse();
    }

    [Fact]
    public void A_setting_the_game_rewrote_is_put_back()
    {
        // Arrange
        var installer = Install();
        File.WriteAllText(_fx.GameFolder.Combine("settings.ini"), "language=ru");

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Read("settings.ini").Should().Be("language=en");
    }

    [Fact]
    public void A_save_the_player_made_is_kept()
    {
        // Arrange
        var installer = Install();
        _fx.GameFolder.File("save/file1.rmmzsave", "the player's progress");

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Read("save/file1.rmmzsave").Should().Be("the player's progress");
    }

    [Fact]
    public void A_large_file_the_game_got_since_is_kept()
    {
        // Arrange
        var installer = Install();
        _fx.GameFolder.File("dlc/content.pak", new byte[GameWriteGuard.MaxSmallFile + 1]);

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Exists("dlc/content.pak").Should().BeTrue();
    }

    [Fact]
    public void The_game_folder_is_left_when_the_game_was_updated_since()
    {
        // Arrange
        var installer = Install();
        File.WriteAllText(_fx.GameFolder.Combine("settings.ini"), "written by the update");
        var updated = _fx.Game with { Version = GameVersion.FromBuildId("2") };

        // Act
        installer.Uninstall(updated, cancellationToken: Ct);

        // Assert
        _fx.Read("settings.ini").Should().Be("written by the update");
    }

    [Fact]
    public void The_uninstall_tells_what_it_undid()
    {
        // Arrange
        var installer = Install();
        File.WriteAllText(_fx.GameFolder.Combine("settings.ini"), "language=ru");

        // Act
        var result = installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        result.GameWrites.Should().BeEquivalentTo(new { Undone = 1, Written = new[] { "settings.ini" } });
    }

    [Fact]
    public void The_pack_files_are_still_put_back_after_the_game_writes()
    {
        // Arrange
        var installer = Install();
        _fx.GameFolder.File("cache.json", "cache");

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        new { Map = _fx.Read("data/Map001.json"), Added = _fx.Exists("data/Fonts.json") }.Should().Be(new { Map = "map 1 original", Added = false });
    }
}
