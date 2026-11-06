using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class PathNamesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _dir.Dispose();

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Directory symlinks need admin rights on Windows")]
    public void Real_path_resolves_a_symlinked_folder_on_the_way()
    {
        // Arrange
        var target = _dir.Dir("real/Steam").Combine("real/Steam");
        Directory.CreateDirectory(Path.Combine(target, "steamapps"));
        var link = _dir.Combine("link");
        Directory.CreateSymbolicLink(link, target);

        // Act
        var real = PathNames.RealPath(Path.Combine(link, "steamapps"));

        // Assert
        real.Should().Be(Path.Combine(PathNames.RealPath(target), "steamapps"));
    }

    [Fact]
    public void Real_path_of_a_missing_folder_is_its_full_path()
    {
        // Arrange
        var missing = Path.Combine(PathNames.RealPath(_dir.Path), "missing", "game");

        // Act
        var real = PathNames.RealPath(missing + Path.DirectorySeparatorChar);

        // Assert
        real.Should().Be(missing);
    }
}
