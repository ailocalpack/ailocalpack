using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Toolkit.Tests.Generation;

/// <summary>Where an engine keeps what its game writes, outside the game folder.</summary>
public sealed class GameDataFoldersTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private OsEnvironment Env(OsKind os) => new(os, _dir.Combine("user"), name => name == "LOCALAPPDATA" ? _dir.Combine("user/AppData/Local") : null);

    [Fact]
    public void A_unity_game_on_windows_writes_under_local_low_by_company_and_product()
    {
        // Arrange
        _dir.File("game/Quest_Data/app.info", "Studio\nQuest\n");

        // Act
        var roots = GameDataFolders.For(_dir.Combine("game"), GameEngine.Unity, "Quest", Env(OsKind.Windows));

        // Assert
        roots.Should().Equal(new GameWriteRoot("unity", Path.Combine(_dir.Combine("user"), "AppData", "LocalLow", "Studio", "Quest")));
    }

    [Fact]
    public void An_unreal_game_writes_its_projects_saved_folder()
    {
        // Arrange
        _dir.Dir("game/Engine").Dir("game/Quest/Content/Paks");

        // Act
        var roots = GameDataFolders.For(_dir.Combine("game"), GameEngine.Unreal, "Quest", Env(OsKind.Windows));

        // Assert
        roots.Should().Equal(new GameWriteRoot("unreal-quest", Path.Combine(_dir.Combine("user/AppData/Local"), "Quest", "Saved")));
    }

    [Fact]
    public void An_rpg_maker_game_writes_only_into_its_own_folder()
    {
        // Act
        var roots = GameDataFolders.For(_dir.Combine("game"), GameEngine.RpgMaker, "Quest", Env(OsKind.Linux));

        // Assert
        roots.Should().BeEmpty();
    }
}
