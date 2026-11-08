using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The Translate stage's chunks: budgets, grouping by file / scene, the game's order (task 50).</summary>
public sealed class ChunkPlannerTests
{
    private static ExtractedString S(string key, string original = "Hello there", string? context = null) => new(key, original, context, null, []);

    private static IEnumerable<ExtractedString> File(string file, int count) =>
        Enumerable.Range(0, count).Select(i => S($"data/{file}.json:/{i}"));

    private static List<int> Sizes(IReadOnlyList<TranslationChunk> chunks) => [.. chunks.Select(c => c.Keys.Count)];

    [Fact]
    public void A_chunk_holds_at_most_the_budgets_strings()
    {
        // Arrange
        var strings = File("Map001", 25).ToList();

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(10, 100_000));

        // Assert
        Sizes(chunks).Should().Equal(10, 10, 5);
    }

    [Fact]
    public void A_chunk_stays_within_the_token_budget()
    {
        // Arrange
        var strings = Enumerable.Range(0, 6).Select(i => S($"data/Map001.json:/{i}", new string('a', 400))).ToList();

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(50, 250));

        // Assert
        Sizes(chunks).Should().Equal(2, 2, 2);
    }

    [Fact]
    public void A_string_larger_than_the_token_budget_gets_a_chunk_of_its_own()
    {
        // Arrange
        var strings = new[] { S("data/Map001.json:/0"), S("data/Map001.json:/1", new string('a', 10_000)), S("data/Map001.json:/2") };

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(50, 500));

        // Assert
        Sizes(chunks).Should().Equal(1, 1, 1);
    }

    [Fact]
    public void The_context_note_counts_towards_the_tokens()
    {
        // Arrange
        var plain = S("k", new string('a', 40));
        var withNote = S("k", new string('a', 40), new string('b', 40));

        // Act
        var difference = ChunkPlanner.Tokens(withNote) - ChunkPlanner.Tokens(plain);

        // Assert
        difference.Should().Be(10);
    }

    [Fact]
    public void A_chunk_at_least_half_full_ends_before_a_file_that_doesnt_fit()
    {
        // Arrange
        var strings = File("Map001", 6).Concat(File("Map002", 6)).ToList();

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(10, 100_000));

        // Assert
        chunks.Select(c => c.Keys.Select(k => k[..12]).Distinct().Count()).Should().Equal(1, 1);
    }

    [Fact]
    public void Small_files_share_a_chunk()
    {
        // Arrange
        var strings = File("Actors", 3).Concat(File("Classes", 2)).Concat(File("Items", 4)).ToList();

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(10, 100_000));

        // Assert
        Sizes(chunks).Should().Equal(9);
    }

    [Fact]
    public void A_chunk_under_half_full_is_filled_from_the_next_file()
    {
        // Arrange
        var strings = File("Actors", 2).Concat(File("Map001", 12)).ToList();

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(10, 100_000));

        // Assert
        Sizes(chunks).Should().Equal(10, 4);
    }

    [Fact]
    public void Strings_come_in_the_games_order_with_numbers_compared_as_numbers()
    {
        // Arrange
        var strings = new[] { S("data/Map001.json:/list/10"), S("data/Map001.json:/list/2"), S("data/Map001.json:/list/1") };

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings);

        // Assert
        chunks.Single().Keys.Should().Equal("data/Map001.json:/list/1", "data/Map001.json:/list/2", "data/Map001.json:/list/10");
    }

    [Fact]
    public void Every_string_is_in_exactly_one_chunk()
    {
        // Arrange
        var strings = File("Map001", 37).Concat(File("Map002", 3)).Concat(File("Troops", 61)).ToList();

        // Act
        var chunks = ChunkPlanner.Plan("rpgmaker", strings, new ChunkBudget(10, 300));

        // Assert
        chunks.SelectMany(c => c.Keys).Should().BeEquivalentTo(strings.Select(s => s.Key)).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_same_strings_give_the_same_chunks()
    {
        // Arrange
        var strings = File("Map001", 37).Concat(File("Troops", 61)).ToList();
        var first = ChunkPlanner.Plan("rpgmaker", strings);

        // Act
        var second = ChunkPlanner.Plan("rpgmaker", [.. Enumerable.Reverse(strings)]);

        // Assert
        second.Select(c => c.Keys).Should().BeEquivalentTo(first.Select(c => c.Keys), o => o.WithStrictOrdering());
    }

    [Fact]
    public void A_budget_without_room_is_refused()
    {
        // Arrange
        var budget = new ChunkBudget(0, 100);

        // Act
        var act = () => ChunkPlanner.Plan("rpgmaker", [S("k")], budget);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("rpgmaker", "data/Map001.json:/events/1/pages/0/list/2/parameters/0", "data/Map001.json")]
    [InlineData("unity", "level1:42/m_Text", "level1")]
    [InlineData("renpy", "say:start_1a2b3c4d", "say:start")]
    [InlineData("renpy", "say:chapter_2_1a2b3c4d_1", "say:chapter_2")]
    [InlineData("renpy", "string:0123456789abcdef", "string")]
    [InlineData("godot", "scene:ui/menu.tscn:Panel/Start:text", "scene:ui/menu.tscn")]
    [InlineData("godot", "tr:MENU_START", "tr")]
    [InlineData("unreal", "locres:Game/Game.locres:Menu:Start", "locres:Game/Game.locres:Menu")]
    [InlineData("other", "a:b:c", "a:b")]
    public void A_key_is_grouped_by_its_file_or_scene(string tool, string key, string group)
    {
        // Arrange
        var keyOfTool = (tool, key);

        // Act
        var result = ChunkPlanner.Group(keyOfTool.tool, keyOfTool.key);

        // Assert
        result.Should().Be(group);
    }

    [Fact]
    public void The_chunk_file_reads_back_what_was_written()
    {
        // Arrange
        using var dir = new TempDir();
        var files = new GenerationFiles(dir.Path);
        IReadOnlyList<TranslationChunk> chunks = [new(["a", "b"]), new(["c"])];
        ChunkPlanFile.Write(files, chunks);

        // Act
        var read = ChunkPlanFile.Read(files);

        // Assert
        read!.Select(c => c.Keys).Should().BeEquivalentTo(chunks.Select(c => c.Keys), o => o.WithStrictOrdering());
    }

    [Fact]
    public void A_chunk_file_repeating_a_key_is_refused()
    {
        // Arrange
        using var dir = new TempDir();
        dir.File("chunks.json", """{"schemaVersion":1,"chunks":[{"keys":["a"]},{"keys":["a"]}]}""");

        // Act
        var act = () => ChunkPlanFile.Read(new GenerationFiles(dir.Path));

        // Assert
        act.Should().Throw<GenerationFileException>().WithMessage("*repeats a key*");
    }
}
