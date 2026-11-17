using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>A job reads and test-runs the game in its own folder (_Live game folder_): the recorded folder, and what deleting the job puts back.</summary>
public sealed class LiveGameFolderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;

    public LiveGameFolderTests()
    {
        _paths = AppPaths.UnderHome(_dir.Combine("home"));
        _dir.File("game/data/System.json", "original");
    }

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private GenerationJobFolders Copies => new(_paths, new FixedDiskSpace(null));

    private string JobFolder => Copies.JobFolder(GameId.Steam(620), "job1");

    private GenerationStepContext Context()
    {
        var job = GenerationJobMachine.Create("job1", GameId.Steam(620), "Quest", "ru", "fake", GenerationOptions.Default, DateTimeOffset.UnixEpoch);
        return new GenerationStepContext(new GenerationJobEntry(JobFolder, job), TimeProvider.System);
    }

    [Fact]
    public void The_recorded_game_folder_reads_back()
    {
        // Arrange
        var files = new GenerationFiles(JobFolder);
        Directory.CreateDirectory(JobFolder);

        // Act
        files.WriteGameFolder(Game);

        // Assert
        files.ReadGameFolder().Should().Be(Path.GetFullPath(Game));
    }

    [Fact]
    public void A_record_of_a_newer_app_is_not_read()
    {
        // Arrange
        _dir.File(Path.GetRelativePath(_dir.Path, Path.Combine(JobFolder, "game.json")), "{\"schemaVersion\":2,\"folder\":\"" + Game.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}");

        // Act
        var folder = new GenerationFiles(JobFolder).ReadGameFolder();

        // Assert
        folder.Should().BeNull();
    }

    [Fact]
    public void A_step_works_on_the_recorded_game_folder()
    {
        // Arrange
        Directory.CreateDirectory(JobFolder);
        new GenerationFiles(JobFolder).WriteGameFolder(Game);

        // Act
        var folder = Context().GameFolder;

        // Assert
        folder.Should().Be(Path.GetFullPath(Game));
    }

    [Fact]
    public void A_job_set_up_before_keeps_its_working_copy()
    {
        // Arrange
        Directory.CreateDirectory(GenerationJobFolders.LegacyCopyFolder(JobFolder));

        // Act
        var folder = Context().GameFolder;

        // Assert
        folder.Should().Be(GenerationJobFolders.LegacyCopyFolder(JobFolder));
    }

    [Fact]
    public void A_step_without_a_recorded_folder_fails()
    {
        // Arrange
        Directory.CreateDirectory(JobFolder);

        // Act
        var act = () => Context().GameFolder;

        // Assert
        act.Should().Throw<GenerationFileException>();
    }

    [Fact]
    public void Deleting_a_job_puts_back_the_game_files_a_crashed_test_run_left_over()
    {
        // Arrange
        _dir.File(Path.GetRelativePath(_dir.Path, Path.Combine(JobFolder, "build/data/System.json")), "translated");
        new GenerationFiles(JobFolder).WriteGameFolder(Game);
        TestRunOverlay.Apply(JobFolder, Game, Path.Combine(JobFolder, "build"), ["data/System.json"], []);

        // Act
        Copies.Delete(JobFolder);

        // Assert
        new { Text = File.ReadAllText(Path.Combine(Game, "data", "System.json")), Job = Directory.Exists(JobFolder) }
            .Should().Be(new { Text = "original", Job = false });
    }
}
