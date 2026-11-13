using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Tests.Recipes;

/// <summary>The strings the agent's own extractor wrote (task 57).</summary>
public sealed class AgentScriptToolTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private AgentScriptTool Tool(string json)
    {
        _dir.File("agent/strings.json", json);
        return new AgentScriptTool(_dir.Combine("agent/strings.json"));
    }

    private RecipeExtraction Extract(string json) => Tool(json).Extract(_dir.Combine("game"), TestContext.Current.CancellationToken);

    [Fact]
    public void Strings_are_read_in_ordinal_key_order_with_their_files()
    {
        // Act
        var extraction = Extract("""{"strings": [{"key": "b", "original": "Two", "file": "data/b.bin"}, {"key": "a", "original": "One", "file": "data/a.bin"}]}""");

        // Assert
        (extraction.Strings.Select(s => s.Key).ToList(), extraction.Files.ToList(), extraction.Tool)
            .Should().BeEquivalentTo((new List<string> { "a", "b" }, new List<string> { "data/a.bin", "data/b.bin" }, "agent-script"));
    }

    [Fact]
    public void Context_length_limit_and_placeholders_are_kept()
    {
        // Act
        var s = Extract("""{"strings": [{"key": "a", "original": "Hi {hero}", "context": "Dialogue", "maxLength": 30, "placeholders": ["{hero}"]}]}""").Strings.Single();

        // Assert
        s.Should().BeEquivalentTo(new ExtractedString("a", "Hi {hero}", "Dialogue", 30, ["{hero}"]));
    }

    [Fact]
    public void A_placeholder_listed_once_counts_every_time_the_original_has_it()
    {
        // Act
        var s = Extract("""{"strings": [{"key": "a", "original": "%s waves to %s for %d!", "placeholders": ["%s", "%d"]}]}""").Strings.Single();

        // Assert
        s.Placeholders.Should().Equal("%s", "%s", "%d");
    }

    [Theory]
    [InlineData("""{"strings": [{"key": "a", "original": "x"}, {"key": "a", "original": "y"}]}""", "listed twice")]
    [InlineData("""{"strings": [{"key": "", "original": "x"}]}""", "\"key\" must be")]
    [InlineData("""{"strings": [{"key": "a", "original": ""}]}""", "\"original\" must be")]
    [InlineData("""{"strings": [{"key": "a", "original": "x", "file": "../x"}]}""", "\"file\" must be")]
    [InlineData("""{"strings": [{"key": "a", "original": "x", "file": "C:/x"}]}""", "\"file\" must be")]
    [InlineData("""{"strings": [{"key": "a", "original": "x", "maxLength": 0}]}""", "\"maxLength\" must be")]
    [InlineData("""{"strings": [{"key": "a", "original": "x", "placeholders": ["{y}"]}]}""", "every placeholder")]
    [InlineData("""{"items": []}""", "\"strings\" array")]
    [InlineData("""not json""", "not valid JSON")]
    public void A_broken_file_is_refused_with_a_reason_the_agent_can_act_on(string json, string reason)
    {
        // Act
        var act = () => Extract(json);

        // Assert
        act.Should().Throw<RecipeException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void A_missing_file_is_refused()
    {
        // Arrange
        var tool = new AgentScriptTool(_dir.Combine("agent/strings.json"));

        // Act
        var act = () => tool.Extract(_dir.Path, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<RecipeException>().Which.Message.Should().Contain("isn't there");
    }

    [Fact]
    public void The_markup_is_the_listed_placeholders_longest_first()
    {
        // Arrange
        var tool = Tool("""{"strings": [{"key": "a", "original": "^C1 x ^C10", "placeholders": ["^C1", "^C10"]}]}""");

        // Act
        var placeholders = tool.Markup.Placeholders("^C10 y ^C1");

        // Assert
        placeholders.Should().Equal("^C10", "^C1");
    }

    [Fact]
    public void A_translation_without_a_placeholder_is_an_error()
    {
        // Arrange
        var tool = Tool("""{"strings": [{"key": "a", "original": "Hi {hero}", "placeholders": ["{hero}"]}]}""");
        var s = tool.Extract(_dir.Path, TestContext.Current.CancellationToken).Strings.Single();

        // Act
        var issues = StringValidator.Validate(s, "Привет", tool.Markup);

        // Assert
        issues.Should().Contain(i => i.Severity == StringIssueSeverity.Error);
    }

    [Fact]
    public void The_toolkit_never_rebuilds_the_agents_strings()
    {
        // Arrange
        var tool = Tool("""{"strings": [{"key": "a", "original": "x"}]}""");

        // Act
        var act = () => tool.Rebuild(_dir.Path, "ru", new Dictionary<string, string> { ["a"] = "у" }, new DirectoryRebuildOutput(_dir.Combine("out")));

        // Assert
        act.Should().Throw<RecipeException>();
    }

    [Fact]
    public void Touched_files_are_the_existing_files_of_the_keys()
    {
        // Arrange
        _dir.File("game/data/a.bin", "x");
        var tool = Tool("""{"strings": [{"key": "a", "original": "x", "file": "data/a.bin"}, {"key": "b", "original": "y", "file": "data/gone.bin"}]}""");

        // Act
        var files = tool.TouchedFiles(_dir.Combine("game"), ["a", "b"]);

        // Assert
        files.Should().Equal("data/a.bin");
    }

    [Fact]
    public void The_fixture_game_is_not_recognized_as_any_engine()
    {
        // Arrange
        CustomFormatFixture.Create(_dir.Combine("game"));

        // Act
        var kind = new AiLocalPack.Toolkit.Engines.EngineDetector().Detect(_dir.Combine("game")).Kind;

        // Assert
        kind.Engine.Should().Be(AiLocalPack.Toolkit.Engines.GameEngine.Unknown);
    }

    [Fact]
    public void The_fixture_format_reads_back_what_it_wrote()
    {
        // Arrange
        var data = CustomFormatFixture.Encode(CustomFormatFixture.Texts);

        // Act
        var texts = CustomFormatFixture.Decode(data);

        // Assert
        texts.Should().Equal(CustomFormatFixture.Texts);
    }
}
