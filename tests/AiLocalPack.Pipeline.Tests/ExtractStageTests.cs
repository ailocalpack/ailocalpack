using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Extract: the toolkit's strings into <c>source.json</c> and the job's counters (task 49).</summary>
public sealed class ExtractStageTests : IDisposable
{
    private readonly StageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ExtractStage Stage(RecipeTools? tools = null) => new(tools ?? RecipeTools.Builtin);

    private Task<GenerationStepResult> RunAsync(RecipeTools? tools = null) =>
        Stage(tools).RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_known_engine_gives_the_strings_and_chunks_of_50()
    {
        // Arrange
        _h.WithRpgMakerCopy();

        // Act
        var result = await RunAsync();

        // Assert
        result.Should().Be(new GenerationStepResult.StageDone(new GenerationCounters(64, 0, 2, 0)));
    }

    [Fact]
    public async Task The_strings_are_stored_with_originals_and_placeholders()
    {
        // Arrange
        _h.WithRpgMakerCopy();

        // Act
        await RunAsync();

        // Assert
        var source = SourceStringsFile.Read(_h.Files)!;
        var extracted = RecipeTools.Builtin.Find("rpgmaker")!.Extract(_h.Copy, TestContext.Current.CancellationToken);
        new { source.Tool, Engine = source.Engine.ToString(), Strings = source.Strings.Count, WithPlaceholders = source.Strings.Count(s => s.Placeholders.Count > 0) }
            .Should().Be(new { Tool = "rpgmaker", Engine = "rpgmaker:mz", Strings = extracted.Strings.Count, WithPlaceholders = extracted.Strings.Count(s => s.Placeholders.Count > 0) });
    }

    [Fact]
    public async Task Translations_already_stored_are_counted()
    {
        // Arrange
        _h.WithRpgMakerCopy();
        var key = RecipeTools.Builtin.Find("rpgmaker")!.Extract(_h.Copy, TestContext.Current.CancellationToken).Strings[0].Key;
        _h.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { [key] = "Перевод", ["gone:key"] = "Старое" }));

        // Act
        var result = await RunAsync();

        // Assert
        ((GenerationStepResult.StageDone)result).Counters!.Translated.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_engine_fails_before_any_agent_runs()
    {
        // Arrange
        _h.Dir.File("work/steam-620/job/game/readme.txt", "hello");

        // Act
        var result = await RunAsync();

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Toolkit);
    }

    [Fact]
    public async Task An_engine_switched_off_on_this_system_fails_with_the_reason()
    {
        // Arrange
        _h.WithRpgMakerCopy();
        var tools = new RecipeTools(RecipeTools.BuiltinTools(), [new EngineKind(GameEngine.RpgMaker)]);

        // Act
        var result = await RunAsync(tools);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Message.Should().StartWith("The toolkit doesn't handle RPG Maker MZ games on this system for now");
    }

    [Fact]
    public async Task A_broken_game_file_fails_the_stage_with_the_file()
    {
        // Arrange
        _h.WithRpgMakerCopy();
        _h.Dir.File("work/steam-620/job/game/data/Items.json", "{ not json");

        // Act
        var result = await RunAsync();

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Should().Match<GenerationStepResult.Failed>(
            f => f.Kind == GenerationFailureKind.Toolkit && f.Message.Contains("Items.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_extraction_is_logged()
    {
        // Arrange
        _h.WithRpgMakerCopy();

        // Act
        await RunAsync();

        // Assert
        _h.Log.Should().Contain("Extracted 64 strings from").And.Contain("with rpgmaker 1.0.0 (RPG Maker MZ): 2 chunks.");
    }
}
