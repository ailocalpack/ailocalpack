using System.IO.Compression;
using AiLocalPack.Core.Dependencies;

namespace AiLocalPack.Core.Tests.Dependencies;

public sealed class DependencyArchiveTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Zip(params (string Name, string Content, int? UnixMode)[] entries)
    {
        var path = _dir.Combine($"{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, content, mode) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (mode is { } unix)
                {
                    entry.ExternalAttributes = unix << 16;
                }

                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return path;
    }

    private string Staging => _dir.Combine("staging");

    [Fact]
    public void Files_are_unpacked_at_their_game_paths()
    {
        // Arrange
        var zip = Zip(("winhttp.dll", "dll", null), ("BepInEx/core/BepInEx.dll", "core", null), ("BepInEx/", "", null));

        // Act
        var files = DependencyArchive.Extract(zip, DependencyLayout.Root, Staging, TestContext.Current.CancellationToken);

        // Assert
        files.Select(f => (f.Path, File.ReadAllText(f.Source))).Should().Equal(("BepInEx/core/BepInEx.dll", "core"), ("winhttp.dll", "dll"));
    }

    [Fact]
    public void Layout_takes_a_folder_of_the_archive_into_a_folder_of_the_game()
    {
        // Arrange
        var zip = Zip(("release/BepInEx/core/a.dll", "a", null), ("readme.txt", "skip", null));

        // Act
        var files = DependencyArchive.Extract(zip, new DependencyLayout("release", "Mods"), Staging, TestContext.Current.CancellationToken);

        // Assert
        files.Select(f => f.Path).Should().Equal("Mods/BepInEx/core/a.dll");
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("a\\evil.dll")]
    public void Name_leaving_the_folder_is_refused_before_anything_is_written(string name)
    {
        // Arrange
        var zip = Zip(("ok.dll", "ok", null), (name, "evil", null));

        // Act
        var act = () => DependencyArchive.Extract(zip, DependencyLayout.Root, Staging, TestContext.Current.CancellationToken);

        // Assert
        (act.Should().Throw<DependencyArchiveException>().Which.Message, Directory.Exists(Staging)).Should().Be(($"The archive entry '{name}' is not a plain relative path.", false));
    }

    [Fact]
    public void Link_entry_is_refused()
    {
        // Arrange
        var zip = Zip(("link", "/etc/passwd", 0xA1FF));

        // Act
        var act = () => DependencyArchive.Extract(zip, DependencyLayout.Root, Staging, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<DependencyArchiveException>().WithMessage("*is a link.");
    }

    [Fact]
    public void Names_differing_only_in_case_are_refused()
    {
        // Arrange
        var zip = Zip(("BepInEx/a.dll", "1", null), ("bepinex/A.dll", "2", null));

        // Act
        var act = () => DependencyArchive.Extract(zip, DependencyLayout.Root, Staging, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<DependencyArchiveException>().WithMessage("*twice*");
    }

    [Fact]
    public void Not_a_zip_is_refused()
    {
        // Arrange
        _dir.File("bad.zip", "not a zip");

        // Act
        var act = () => DependencyArchive.Extract(_dir.Combine("bad.zip"), DependencyLayout.Root, Staging, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<DependencyArchiveException>().WithMessage("bad.zip is not a readable ZIP archive*");
    }

    [Fact]
    public void Archive_without_files_under_the_layout_is_refused()
    {
        // Arrange
        var zip = Zip(("other/a.dll", "a", null));

        // Act
        var act = () => DependencyArchive.Extract(zip, new DependencyLayout("release", ""), Staging, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<DependencyArchiveException>().WithMessage("The archive holds no file under 'release/'.");
    }
}
