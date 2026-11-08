using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Prompts as versioned resources, rendered and snapshot-tested (task 49).</summary>
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
