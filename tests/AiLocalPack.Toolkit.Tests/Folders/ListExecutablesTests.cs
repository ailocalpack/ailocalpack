using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Tests.Folders;

public sealed class ListExecutablesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Main_executable_comes_first_then_the_others()
    {
        // Arrange
        _dir.RenPy();

        // Act
        var list = FolderRecognizer.ListExecutables(_dir.Path, "Game.sh");

        // Assert
        list.Should().Equal("Game.sh", "Game-32.exe", "Game.exe");
    }

    [Fact]
    public void Crash_handlers_and_installers_are_left_out()
    {
        // Arrange
        _dir.UnityWindows().File("setup.exe", "MZ").File("unins000.exe", "MZ");

        // Act
        var list = FolderRecognizer.ListExecutables(_dir.Path, "Game.exe");

        // Assert
        list.Should().Equal("Game.exe");
    }

    [Fact]
    public void Binaries_subfolders_are_listed()
    {
        // Arrange
        _dir.File("launcher.exe", "MZ").File("bin/x64/game.exe", "MZ");

        // Act
        var list = FolderRecognizer.ListExecutables(_dir.Path, null);

        // Assert
        list.Should().Equal("launcher.exe", "bin/x64/game.exe");
    }

    [Fact]
    public void Bundle_lists_its_macos_binaries()
    {
        // Arrange
        _dir.File("Contents/Info.plist", "x").File("Contents/MacOS/Game", "macho").File("Contents/MacOS/Helper", "macho");

        // Act
        var list = FolderRecognizer.ListExecutables(_dir.Path, "Contents/MacOS/Game");

        // Assert
        list.Should().Equal("Contents/MacOS/Game", "Contents/MacOS/Helper");
    }

    [Fact]
    public void Missing_folder_lists_only_the_given_executable()
    {
        // Arrange
        var missing = _dir.Combine("gone");

        // Act
        var list = FolderRecognizer.ListExecutables(missing, "Game.exe");

        // Assert
        list.Should().Equal("Game.exe");
    }
}
