using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Tests.Install;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>
/// Working copies and the estimate's reads take the game without the pack installed in its folder (task 56): the
/// pack's files come from its backup, the files it added are left out, a file the game changed since stays the game's.
/// </summary>
public sealed class WorkingCopyWithoutPackTests : IDisposable
{
    private readonly InstallFixture _fx = new();

    public WorkingCopyWithoutPackTests()
    {
        _fx.Installer().Install(
            _fx.Request(("data/Map001.json", "map 1 RU"), ("data/System.json", "system RU"), ("data/ru.json", "added by the pack")),
            null,
            cancellationToken: Ct);
    }

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WorkingCopies Copies => new(_fx.Paths, new NoClones(), new FixedDiskSpace(null));

    private string JobFolder => Copies.JobFolder(InstallFixture.Id, "job1");

    private Dictionary<string, string> CopyOf()
    {
        var copies = Copies;
        var copy = copies.Create(copies.Plan(_fx.GameFolder.Path, WorkingCopyScope.AllFiles, Ct), JobFolder, cancellationToken: Ct);
        return Directory.EnumerateFiles(copy.Folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(copy.Folder, f).Replace('\\', '/'), File.ReadAllText);
    }

    [Fact]
    public void The_copy_holds_the_originals_of_the_pack_files_without_the_files_it_added()
    {
        // Act
        var files = CopyOf();

        // Assert
        files.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["data/Map001.json"] = "map 1 original",
            ["data/Map002.json"] = "map 2 original",
            ["data/System.json"] = "system original",
            ["js/plugins.js"] = "plugins original",
            ["Game.exe"] = "MZ game",
        });
    }

    [Fact]
    public void A_file_the_game_rewrote_after_the_install_stays_the_games()
    {
        // Arrange
        File.WriteAllText(_fx.GameFolder.Combine("data/Map001.json"), "map 1 updated");

        // Act
        var files = CopyOf();

        // Assert
        files["data/Map001.json"].Should().Be("map 1 updated");
    }

    [Fact]
    public void The_plan_names_what_it_takes_from_the_backup()
    {
        // Act
        var plan = Copies.Plan(_fx.GameFolder.Path, WorkingCopyScope.AllFiles, Ct);

        // Assert
        (plan.WithoutPack.Restored.Keys.Order(StringComparer.Ordinal), plan.WithoutPack.Added).Should().BeEquivalentTo(
            (new[] { "data/Map001.json", "data/System.json" }, new HashSet<string> { "data/ru.json" }));
    }

    [Fact]
    public void A_read_without_the_pack_sees_the_originals()
    {
        // Act
        var text = Copies.ReadWithoutPack(_fx.GameFolder.Path, folder => File.ReadAllText(Path.Combine(folder, "data", "System.json")), Ct);

        // Assert
        text.Should().Be("system original");
    }

    [Fact]
    public void A_read_without_the_pack_leaves_no_scratch_copy()
    {
        // Act
        Copies.ReadWithoutPack(_fx.GameFolder.Path, _ => 0, Ct);

        // Assert
        Directory.Exists(Path.Combine(_fx.Paths.WorkingCopies, ".scratch")).Should().BeFalse();
    }

    [Fact]
    public void A_game_without_a_pack_is_read_in_place()
    {
        // Arrange
        using var other = new TempDir();
        other.File("data/System.json", "other");

        // Act
        var folder = Copies.ReadWithoutPack(other.Path, f => f, Ct);

        // Assert
        folder.Should().Be(Path.GetFullPath(other.Path));
    }
}
