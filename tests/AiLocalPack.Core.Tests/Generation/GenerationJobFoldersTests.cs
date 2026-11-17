using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>The folders of generation jobs (_Live game folder_: the game itself is never copied).</summary>
public sealed class GenerationJobFoldersTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;

    public GenerationJobFoldersTests()
    {
        _paths = AppPaths.UnderHome(_dir.Combine("home"));
        _dir.File("game/Game.exe", "MZ program").File("game/www/data/System.json", "{\"gameTitle\":\"Quest\"}");
    }

    public static bool HostIsNotMac => !OperatingSystem.IsMacOS();

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private GenerationJobFolders Jobs(long? free = null) => new(_paths, new FixedDiskSpace(free));

    private string JobFolder => Jobs().JobFolder(GameId.Steam(620), "job1");

    [Fact]
    public void A_job_needs_only_the_reserve_and_the_margin()
    {
        // Act
        var space = Jobs(free: 10L << 30).CheckSpace(1000);

        // Assert
        space.Should().Be(new JobSpace(1000 + GenerationJobFolders.Margin, 10L << 30));
    }

    [Fact]
    public void Too_little_free_space_is_not_enough()
    {
        // Act
        var space = Jobs(free: 1024).CheckSpace(1000);

        // Assert
        space.IsEnough.Should().BeFalse();
    }

    [Fact]
    public void Delete_removes_the_job_folder_with_read_only_files_and_its_empty_game_folder()
    {
        // Arrange
        _dir.File(Path.GetRelativePath(_dir.Path, Path.Combine(JobFolder, "strings.json")), "{}");
        File.SetAttributes(Path.Combine(JobFolder, "strings.json"), FileAttributes.ReadOnly);

        // Act
        Jobs().Delete(JobFolder);

        // Assert
        Directory.Exists(Path.Combine(_paths.GenerationJobs, "steam-620")).Should().BeFalse();
    }

    [Fact]
    public void Delete_refuses_a_folder_outside_the_job_folders()
    {
        // Act
        var act = () => Jobs().Delete(Game);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_copy_an_older_job_made_is_deleted_and_the_jobs_files_stay()
    {
        // Arrange
        _dir.File(Path.GetRelativePath(_dir.Path, Path.Combine(JobFolder, "game", "Game.exe")), "MZ")
            .File(Path.GetRelativePath(_dir.Path, Path.Combine(JobFolder, "game-originals", "settings.ini")), "a")
            .File(Path.GetRelativePath(_dir.Path, Path.Combine(JobFolder, "strings.json")), "{}");

        // Act
        GenerationJobFolders.DeleteLegacyCopy(JobFolder);

        // Assert
        new { Copy = Directory.Exists(Path.Combine(JobFolder, "game")), Saved = Directory.Exists(Path.Combine(JobFolder, "game-originals")), Strings = File.Exists(Path.Combine(JobFolder, "strings.json")) }
            .Should().Be(new { Copy = false, Saved = false, Strings = true });
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:x")]
    public void A_job_ID_with_path_parts_is_refused(string jobId)
    {
        // Act
        var act = () => Jobs().JobFolder(GameId.Steam(620), jobId);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void New_job_IDs_start_with_the_UTC_time()
    {
        // Arrange
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 8, 13, 5, 9, TimeSpan.Zero));

        // Act
        var id = GenerationJobFolders.NewJobId(clock);

        // Assert
        id.Should().MatchRegex("^20261108-130509-[0-9a-f]{6}$");
    }

    [Fact(SkipWhen = nameof(HostIsNotMac), Skip = "APFS clonefile")]
    public void The_system_cloner_clones_on_APFS()
    {
        // Arrange
        var target = _dir.Combine("clone.json");

        // Act
        var cloned = FileCloner.Create().TryClone(_dir.Combine("game/www/data/System.json"), target);

        // Assert
        new { cloned, text = File.ReadAllText(target) }.Should().BeEquivalentTo(new { cloned = true, text = "{\"gameTitle\":\"Quest\"}" });
    }

    [Fact]
    public void The_system_cloner_never_overwrites_an_existing_file()
    {
        // Arrange
        var target = _dir.File("taken.txt", "mine").Combine("taken.txt");

        // Act
        var cloned = FileCloner.Create().TryClone(_dir.Combine("game/Game.exe"), target);

        // Assert
        new { cloned, text = File.ReadAllText(target) }.Should().BeEquivalentTo(new { cloned = false, text = "mine" });
    }

    [Fact]
    public void Movies_and_audio_are_media()
    {
        // Act
        var media = new[] { "movies/intro.webm", "audio/bgm/theme.ogg", "data/System.json" }.Select(MediaFiles.IsMedia);

        // Assert
        media.Should().Equal(true, true, false);
    }
}

/// <summary>A file system without clones: every file is copied.</summary>
internal sealed class NoClones : IFileCloner
{
    public bool TryClone(string source, string destination) => false;
}

/// <summary>Free space as given (<c>null</c> = unknown).</summary>
internal sealed class FixedDiskSpace(long? free) : IDiskSpace
{
    public long? FreeBytes(string path) => free;
}
