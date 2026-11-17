using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Setup makes no copy of the game (_Live game folder_): it records the game folder for the later stages.</summary>
public sealed class SetupStageTests : IDisposable
{
    private readonly StageHarness _h = new();

    public SetupStageTests()
    {
        _h.Dir.RpgMakerGame("games/sample");
    }

    public void Dispose() => _h.Dispose();

    private string GameFolder => _h.Dir.Combine("games/sample");

    private Task<GenerationStepResult> RunAsync(long? free = null)
    {
        var jobs = new GenerationJobFolders(AppPaths.UnderHome(_h.Dir.Combine("home")), new FreeSpace(free));
        var game = StageHarness.Game with { InstallPath = GameFolder };
        var context = new GenerationStepContext(new GenerationJobEntry(_h.Folder, _h.Context(GenerationStage.Setup).Job), _h.Clock);
        File.Delete(_h.Files.GamePath);
        return new SetupStage(game, new GenerationEstimator(RecipeTools.Builtin, jobs)).RunStepAsync(context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Setup_records_the_game_folder_and_makes_no_copy()
    {
        // Act
        var result = await RunAsync();

        // Assert
        new { result, Folder = _h.Files.ReadGameFolder(), Copy = Directory.Exists(_h.Copy) }.Should().BeEquivalentTo(new
        {
            result = new GenerationStepResult.StageDone(),
            Folder = Path.GetFullPath(GameFolder),
            Copy = false,
        });
    }

    [Fact]
    public async Task Setup_deletes_the_copy_an_older_job_made()
    {
        // Arrange
        _h.Dir.File("work/steam-620/job/game/Game.exe", "MZ");

        // Act
        await RunAsync();

        // Assert
        Directory.Exists(_h.Copy).Should().BeFalse();
    }

    [Fact]
    public async Task Setup_fails_when_the_disk_has_no_room_for_the_jobs_files()
    {
        // Act
        var result = await RunAsync(free: 1024);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Disk);
    }

    private sealed class FreeSpace(long? free) : IDiskSpace
    {
        public long? FreeBytes(string path) => free;
    }
}
