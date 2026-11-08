using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Tests.Generation;

public sealed class GenerationEstimatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly WorkingCopies _copies;

    public GenerationEstimatorTests() =>
        _copies = new WorkingCopies(AppPaths.UnderHome(_dir.Combine("home")), new NoClones(), new FreeSpace());

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private GenerationEstimator Estimator(RecipeTools? tools = null) => new(tools ?? RecipeTools.Builtin, _copies);

    [Fact]
    public void A_toolkit_engine_is_counted_from_its_extracted_strings()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var extraction = RecipeTools.Builtin.For(GameEngine.RpgMaker)!.Extract(Game, Ct);

        // Act
        var estimate = Estimator().Estimate(Game, cancellationToken: Ct);

        // Assert
        estimate.Should().BeEquivalentTo(new
        {
            Source = TextSizeSource.Toolkit,
            Work = new { Basis = EstimateBasis.Toolkit, Text = TextSize.Of(extraction.Strings.Select(s => s.Original)) },
            Copy = new { Scope = WorkingCopyScope.WithoutMedia },
        });
    }

    [Fact]
    public void The_reserve_is_the_size_of_the_files_the_strings_come_from()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var extraction = RecipeTools.Builtin.For(GameEngine.RpgMaker)!.Extract(Game, Ct);
        var size = extraction.Files.Sum(f => new FileInfo(Path.Combine(Game, f)).Length);

        // Act
        var estimate = Estimator().Estimate(Game, cancellationToken: Ct);

        // Assert
        estimate.Should().BeEquivalentTo(new { ReserveBytes = size, Space = new { NeededBytes = estimate.Copy.TotalBytes + size + WorkingCopies.Margin } });
    }

    [Fact]
    public void An_unknown_engine_is_guessed_from_file_sizes_and_copied_whole()
    {
        // Arrange
        _dir.File("game/Game.exe", "MZ").File("game/data/dialogue.json", new string('a', 100_000)).File("game/music.ogg", "OggS");

        // Act
        var estimate = Estimator().Estimate(Game, cancellationToken: Ct);

        // Assert
        estimate.Should().BeEquivalentTo(new
        {
            Source = TextSizeSource.UnknownEngine,
            Work = new { Basis = EstimateBasis.FileSizes, Text = new { Characters = 1000L } },
            Copy = new { Scope = WorkingCopyScope.AllFiles, SkippedFiles = 0 },
        });
    }

    [Fact]
    public void An_engine_switched_off_on_this_system_is_guessed()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var tools = new RecipeTools(RecipeTools.BuiltinTools(), [new EngineKind(GameEngine.RpgMaker)]);

        // Act
        var estimate = Estimator(tools).Estimate(Game, cancellationToken: Ct);

        // Assert
        estimate.Should().BeEquivalentTo(new { Source = TextSizeSource.EngineDisabled, Tool = (IRecipeTool?)null, Work = new { Basis = EstimateBasis.FileSizes } });
    }

    [Fact]
    public void A_game_the_toolkit_fails_to_read_is_guessed_with_the_reason()
    {
        // Arrange
        _dir.RpgMakerGame("game").File("game/data/Items.json", "{ broken");

        // Act
        var estimate = Estimator().Estimate(Game, cancellationToken: Ct);

        // Assert
        estimate.Should().BeEquivalentTo(new { Source = TextSizeSource.ToolkitFailed, Work = new { Basis = EstimateBasis.FileSizes } });
        estimate.ToolkitError.Should().Contain("Items.json");
    }

    [Fact]
    public void A_stored_engine_is_used_without_detecting_again()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var estimate = Estimator().Estimate(Game, new EngineKind(GameEngine.Unity), cancellationToken: Ct);

        // Assert
        estimate.Should().BeEquivalentTo(new { Engine = new EngineKind(GameEngine.Unity), Source = TextSizeSource.ToolkitFailed });
    }

    [Fact]
    public void A_missing_game_folder_is_refused()
    {
        // Arrange
        var estimator = Estimator();

        // Act
        var act = () => estimator.Estimate(Game, cancellationToken: Ct);

        // Assert
        act.Should().Throw<WorkingCopyException>().Which.Problem.Should().Be(WorkingCopyProblem.GameMissing);
    }

    private sealed class NoClones : IFileCloner
    {
        public bool TryClone(string source, string destination) => false;
    }

    private sealed class FreeSpace : IDiskSpace
    {
        public long? FreeBytes(string path) => 1L << 40;
    }
}
