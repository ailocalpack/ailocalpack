using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Launch;

public sealed class GameProcessMatcherTests
{
    [Fact]
    public void Process_in_the_game_folder_matches()
    {
        // Act
        var inside = GameProcessMatcher.IsInside("/games/Lantern/bin/Lantern", "/games/Lantern", OsKind.Linux);

        // Assert
        inside.Should().BeTrue();
    }

    [Fact]
    public void Process_outside_the_game_folder_does_not_match()
    {
        // Act
        var inside = GameProcessMatcher.IsInside("/usr/bin/steam", "/games/Lantern", OsKind.Linux);

        // Assert
        inside.Should().BeFalse();
    }

    [Fact]
    public void Process_in_a_sibling_with_the_same_prefix_does_not_match()
    {
        // Act
        var inside = GameProcessMatcher.IsInside("/games/Lantern2/Lantern2", "/games/Lantern", OsKind.Linux);

        // Assert
        inside.Should().BeFalse();
    }

    [Fact]
    public void Trailing_separator_of_the_folder_is_ignored()
    {
        // Act
        var inside = GameProcessMatcher.IsInside("/games/Lantern/Lantern", "/games/Lantern/", OsKind.Linux);

        // Assert
        inside.Should().BeTrue();
    }

    [Fact]
    public void Case_matters_on_linux()
    {
        // Act
        var inside = GameProcessMatcher.IsInside("/games/lantern/Lantern", "/games/Lantern", OsKind.Linux);

        // Assert
        inside.Should().BeFalse();
    }

    [Fact]
    public void Case_is_ignored_on_windows()
    {
        // Act
        var inside = GameProcessMatcher.IsInside(@"C:\GAMES\Lantern\Lantern.exe", @"c:\games\lantern", OsKind.Windows);

        // Assert
        inside.Should().BeTrue();
    }

    [Fact]
    public void Case_is_ignored_on_macos()
    {
        // Act
        var inside = GameProcessMatcher.IsInside("/Games/Lantern.app/Contents/MacOS/Lantern", "/games/lantern.app", OsKind.MacOS);

        // Assert
        inside.Should().BeTrue();
    }

    [Fact]
    public void Windows_accepts_either_separator_and_the_long_path_prefix()
    {
        // Act
        var inside = GameProcessMatcher.IsInside(@"\\?\D:\Steam\steamapps\common\Lantern\Lantern.exe", "D:/Steam/steamapps/common/Lantern", OsKind.Windows);

        // Assert
        inside.Should().BeTrue();
    }

    [Fact]
    public void Find_keeps_only_the_processes_of_the_game()
    {
        // Arrange
        RunningProcess[] processes =
        [
            new(1, "/usr/bin/steam"),
            new(2, "/games/Lantern/Lantern.x86_64"),
            new(3, "/games/Lantern/tools/crash-handler"),
            new(4, "/games/Lantern-Demo/Lantern.x86_64"),
        ];

        // Act
        var found = GameProcessMatcher.Find(processes, "/games/Lantern", OsKind.Linux);

        // Assert
        found.Select(p => p.Id).Should().Equal(2, 3);
    }
}
