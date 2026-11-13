using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Prompts as versioned resources, rendered and snapshot-tested (tasks 49, 50).</summary>
public sealed class PromptTemplateTests
{
    private static Dictionary<string, string> GlossaryValues =>
        PromptValues.Glossary("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12);

    [Fact]
    public void The_glossary_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = GlossaryValues;

        // Act
        var prompt = PromptTemplate.Glossary.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("glossary.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_glossary_update_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.GlossaryUpdate("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12, "Фонарщики", 1_180);

        // Act
        var prompt = PromptTemplate.GlossaryUpdate.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("glossary-update.v1.ru.txt", prompt));
    }

    private static Dictionary<string, string> TranslateValues(bool retry)
    {
        var source = new SourceStrings(new EngineKind(GameEngine.RpgMaker, "mz"), "rpgmaker", new Version(1, 0, 0), ["data/Map001.json"], []);
        Toolkit.Recipes.ExtractedString[] strings =
        [
            new("data/Map001.json:/events/1/pages/0/list/1/parameters/0", "\\N[1], drink this Potion.", "Line said by Harold", null, ["\\N[1]"]),
            new("data/Map001.json:/events/1/pages/0/list/2/parameters/0", "Thanks!", "Line said by Mira", 20, []),
        ];
        Dictionary<string, string?>? problems = retry
            ? new() { [strings[0].Key] = null, [strings[1].Key] = "the stored translation \"Спасибо огромнейшее тебе!\" is refused: The text is 25 characters long; the limit is 20." }
            : null;
        return TranslateStage.Values(
            "Sample Quest",
            "ru",
            source,
            2,
            7,
            strings,
            [new("Potion", "Зелье", "heals 50 HP", "noun", "neuter")],
            "informal, ты",
            problems);
    }

    [Fact]
    public void The_translate_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = TranslateValues(retry: false);

        // Act
        var prompt = PromptTemplate.Translate.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("translate.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_translate_retry_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = TranslateValues(retry: true);

        // Act
        var prompt = PromptTemplate.TranslateRetry.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("translate-retry.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_study_prompt_with_web_lookup_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.Study("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12, webLookup: true);

        // Act
        var prompt = PromptTemplate.Study.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("study.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_study_prompt_without_web_lookup_says_so()
    {
        // Arrange
        var values = PromptValues.Study("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12, webLookup: false);

        // Act
        var prompt = PromptTemplate.Study.Render(values);

        // Assert
        prompt.Should().Contain("2. You have no web access: work from the strings alone.");
    }

    [Fact]
    public void The_brief_system_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.SystemBrief("## Setting\nA flooded megacity.");

        // Act
        var prompt = PromptTemplate.SystemBrief.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system-brief.v1.txt", prompt));
    }

    [Fact]
    public void The_system_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.System("Sample Quest", "pt-BR");

        // Act
        var prompt = PromptTemplate.System.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system.v1.pt-BR.txt", prompt));
    }

    [Fact]
    public void The_scripts_system_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.SystemScripts("Lantern Tale", "ru", "/data/work/fp-ab/job/game");

        // Act
        var prompt = PromptTemplate.SystemScripts.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system-scripts.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_unknown_engine_extract_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.ExtractUnknown("Lantern Tale", "ru", EngineKind.Unknown, "/data/work/fp-ab/job/game", 5);

        // Act
        var prompt = PromptTemplate.ExtractUnknown.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("extract-unknown.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_unknown_engine_rebuild_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.RebuildUnknown("Lantern Tale", "ru", "/data/work/fp-ab/job/game", 1_234);

        // Act
        var prompt = PromptTemplate.RebuildUnknown.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("rebuild-unknown.v1.ru.txt", prompt));
    }

    [Fact]
    public void A_missing_field_is_refused()
    {
        // Arrange
        var values = GlossaryValues;
        values.Remove("stringCount");

        // Act
        var act = () => PromptTemplate.Glossary.Render(values);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("The glossary v1 prompt needs a value for {{stringCount}}.*");
    }

    [Fact]
    public void A_value_the_prompt_has_no_field_for_is_refused()
    {
        // Arrange
        var values = PromptValues.System("Sample Quest", "ru");
        values["tone"] = "formal";

        // Act
        var act = () => PromptTemplate.System.Render(values);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("The system v1 prompt has no {{tone}} field.*");
    }

    [Fact]
    public void A_rendered_prompt_keeps_no_field_marks()
    {
        // Arrange
        var values = GlossaryValues;

        // Act
        var prompt = PromptTemplate.Glossary.Render(values);

        // Assert
        prompt.Should().NotContain("{{").And.NotContain("}}");
    }

    [Fact]
    public void A_prompt_is_labelled_with_its_version()
    {
        // Arrange
        var prompt = PromptTemplate.Glossary;

        // Act
        var label = prompt.Label;

        // Assert
        label.Should().Be("glossary v1");
    }

    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("de", "German")]
    [InlineData("qx-Zzzz", "qx-Zzzz")]
    public void Languages_are_named_in_English(string tag, string name)
    {
        // Arrange
        var language = tag;

        // Act
        var result = PromptValues.LanguageName(language);

        // Assert
        result.Should().Be(name);
    }
}
