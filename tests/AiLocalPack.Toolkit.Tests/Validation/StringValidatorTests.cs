using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Tests.Validation;

public sealed class StringValidatorTests
{
    private static ExtractedString Source(string original, int? maxLength = null, params string[] placeholders) =>
        new("k", original, null, maxLength, placeholders);

    [Fact]
    public void Good_translation_has_no_issues()
    {
        // Arrange
        var source = Source(@"Welcome, \N[1].", null, @"\N[1]");

        // Act
        var issues = StringValidator.Validate(source, @"Привет, \N[1].");

        // Assert
        issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_translation_is_an_error(string translation)
    {
        // Arrange
        var source = Source("Potion");

        // Act
        var issues = StringValidator.Validate(source, translation);

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Error, "empty", "The translation is empty."));
    }

    [Fact]
    public void Missing_placeholder_is_an_error()
    {
        // Arrange
        var source = Source(@"\C[2]Rare!\C[0]", null, @"\C[2]", @"\C[0]");

        // Act
        var issues = StringValidator.Validate(source, @"\C[2]Редкость!");

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Error, "placeholder-missing"));
    }

    [Fact]
    public void Placeholder_repeated_in_the_original_must_repeat_in_the_translation()
    {
        // Arrange
        var source = Source("%1 and %1", null, "%1", "%1");

        // Act
        var issues = StringValidator.Validate(source, "%1 и он же");

        // Assert
        issues.Single().Message.Should().Be("Placeholder %1 appears 2 time(s) in the original but 1 in the translation; keep it exactly as written.");
    }

    [Fact]
    public void Placeholders_may_move()
    {
        // Arrange
        var source = Source("{0} of {1}", null, "{0}", "{1}");

        // Act
        var issues = StringValidator.Validate(source, "{1}: {0}");

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Translation_over_the_limit_is_an_error()
    {
        // Arrange
        var source = Source("Potion", maxLength: 5);

        // Act
        var issues = StringValidator.Validate(source, "Зелье!");

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Error, "too-long", "The translation has 6 characters; the game allows at most 5."));
    }

    [Fact]
    public void Limit_counts_combining_marks_with_their_letter()
    {
        // Arrange
        var source = Source("Hello", maxLength: 5);

        // Act
        var issues = StringValidator.Validate(source, "Йо́жик");

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Same_text_as_the_original_is_a_warning()
    {
        // Arrange
        var source = Source("OK");

        // Act
        var issues = StringValidator.Validate(source, "OK");

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Warning, "untranslated"));
    }

    [Fact]
    public void Different_leading_whitespace_is_a_warning()
    {
        // Arrange
        var source = Source(" attacks!");

        // Act
        var issues = StringValidator.Validate(source, "атакует!");

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Warning, "whitespace"));
    }

    [Fact]
    public void Different_line_break_count_is_a_warning()
    {
        // Arrange
        var source = Source("A young knight\nfrom the north.");

        // Act
        var issues = StringValidator.Validate(source, "Юный рыцарь с севера.");

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Warning, "line-breaks", "The original has 1 line break(s), the translation 0."));
    }
}
