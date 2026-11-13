using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Tests.Validation;

public sealed class StringValidatorTests
{
    private static TextMarkup Markup(string tool) => RecipeTools.Builtin.Find(tool)!.Markup;

    private static ExtractedString Source(string tool, string original, int? maxLength = null, LengthUnit unit = LengthUnit.Characters) =>
        new("k", original, null, maxLength, Markup(tool).Placeholders(original)) { MaxLengthUnit = unit };

    private static IReadOnlyList<StringIssue> Validate(string tool, string original, string translation, int? maxLength = null, LengthUnit unit = LengthUnit.Characters) =>
        StringValidator.Validate(Source(tool, original, maxLength, unit), translation, Markup(tool));

    [Fact]
    public void Good_translation_has_no_issues()
    {
        // Arrange
        var source = Source("rpgmaker", @"Welcome, \N[1].");

        // Act
        var issues = StringValidator.Validate(source, @"Привет, \N[1].", Markup("rpgmaker"));

        // Assert
        issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_translation_is_an_error(string translation)
    {
        // Arrange
        var source = Source("rpgmaker", "Potion");

        // Act
        var issues = StringValidator.Validate(source, translation, Markup("rpgmaker"));

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Error, "empty", "The translation is empty."));
    }

    [Theory]
    [InlineData("rpgmaker", @"\C[2]Rare!\C[0]", @"\C[2]Редкость!")]
    [InlineData("rpgmaker", @"%1 gains %2 EXP", "%1 получает опыт")]
    [InlineData("renpy", "Hi, [player_name]!", "Привет!")]
    [InlineData("renpy", "{b}Wake up{/b}", "{b}Проснись")]
    [InlineData("renpy", "%(count)d coins", "монеты")]
    [InlineData("godot", "[color=red]Danger[/color]", "Опасно")]
    [InlineData("godot", "Score: {score}", "Счёт:")]
    [InlineData("unity", "<b>Start</b>", "Начать")]
    [InlineData("unity", "[ff0000]Hot[-]", "Горячо")]
    [InlineData("unity", "{0:N2} gold", "золото")]
    [InlineData("unreal", "{Count} items", "предметы")]
    [InlineData("unreal", "<Bold>Quit</>", "Выход")]
    public void Missing_placeholder_or_tag_is_an_error(string tool, string original, string translation)
    {
        // Act
        var issues = Validate(tool, original, translation);

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Contain((StringIssueSeverity.Error, "placeholder-missing"));
    }

    [Fact]
    public void A_placeholder_listed_once_but_repeated_in_the_original_may_repeat_in_the_translation()
    {
        // Arrange
        var source = new ExtractedString("k", "%s waves to %s!", null, null, ["%s"]);
        var markup = new TextMarkup(new System.Text.RegularExpressions.Regex("%s"), TagBrackets.None);

        // Act
        var issues = StringValidator.Validate(source, "%s машет %s!", markup);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Placeholder_repeated_in_the_original_must_repeat_in_the_translation()
    {
        // Act
        var issues = Validate("rpgmaker", "%1 and %1", "%1 и он же");

        // Assert
        issues.Single().Message.Should().Be("Placeholder %1 appears 2 time(s) in the original but 1 in the translation; keep it exactly as written.");
    }

    [Theory]
    [InlineData("rpgmaker", "Potion", @"\C[2]Зелье")]
    [InlineData("renpy", "Hello", "Привет, [name]")]
    [InlineData("renpy", "Hello", "{i}Привет")]
    [InlineData("godot", "Lives: %d", "Жизни: %d из %d")]
    [InlineData("unity", "{0} of {1}", "{0} из {1} ({2})")]
    [InlineData("unreal", "Quit", "<Bold>Выход")]
    public void Added_placeholder_or_tag_is_an_error(string tool, string original, string translation)
    {
        // Act
        var issues = Validate(tool, original, translation);

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Error, "placeholder-extra"));
    }

    [Fact]
    public void Added_placeholder_names_it_with_both_counts()
    {
        // Act
        var issues = Validate("godot", "Lives: %d", "Жизни: %d из %d");

        // Assert
        issues.Single().Message.Should().Be("Placeholder %d appears 2 time(s) in the translation but 1 in the original; don't add placeholders or tags the original doesn't have.");
    }

    [Theory]
    [InlineData("unity", "{0} of {1}", "{1}: {0}")]
    [InlineData("unreal", "{Name} hits {Target}", "{Target} получает удар от {Name}")]
    [InlineData("renpy", "%(a)s and %(b)s", "%(b)s и %(a)s")]
    [InlineData("godot", "%s and %s", "%s и %s")]
    public void Named_and_numbered_placeholders_may_move(string tool, string original, string translation)
    {
        // Act
        var issues = Validate(tool, original, translation);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Unnumbered_printf_fields_must_keep_their_order()
    {
        // Act
        var issues = Validate("godot", "%s has %d coins", "%d монет у %s");

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Error, "placeholder-order",
            "The fields %s %d must stay in this order (the translation has %d %s): the game fills them by position."));
    }

    [Theory]
    [InlineData("renpy", "{b}Bold{/b} and {i}it{/i}", "{b}Ж{i}{/b} к{/i}")]
    [InlineData("renpy", "{=red}Stop{/=red}", "{/=red}Стоп{=red}")]
    [InlineData("godot", "[color=red]Hot[/color]", "[/color]Горячо[color=red]")]
    [InlineData("godot", "[b][i]Both[/i][/b]", "[b][i]Оба[/b][/i]")]
    [InlineData("unity", "<b>Start</b>", "</b>Начать<b>")]
    [InlineData("unity", "[ff0000]Hot[-]", "[-]Горячо[ff0000]")]
    [InlineData("unreal", "<Bold>Quit</>", "</>Выход<Bold>")]
    public void Unbalanced_tags_are_an_error(string tool, string original, string translation)
    {
        // Act
        var issues = Validate(tool, original, translation);

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Error, "tag-unbalanced"));
    }

    [Fact]
    public void Crossed_tags_name_the_tag_closed_too_early()
    {
        // Act
        var issues = Validate("renpy", "{b}Bold{/b} and {i}it{/i}", "{b}Ж{i}{/b} к{/i}");

        // Assert
        issues.Single().Message.Should().Be("Tag {/b} comes before the closing tag of {i}; close tags in the reverse order they were opened.");
    }

    [Fact]
    public void Added_tags_crossing_the_original_ones_are_worded_as_a_crossing()
    {
        // Act
        var issues = Validate("renpy", "{b}Good Ending{/b}.", "{b}Хорошая {i}концовка{/b}{/i}.");

        // Assert
        issues.Last().Should().Be(new StringIssue(StringIssueSeverity.Error, "tag-unbalanced",
            "Tag {/b} comes before the closing tag of {i}; close tags in the reverse order they were opened."));
    }

    [Fact]
    public void Tag_closed_before_it_opens_closes_nothing()
    {
        // Act
        var issues = Validate("unity", "<b>Start</b>", "</b>Начать<b>");

        // Assert
        issues.Single().Message.Should().Be("Tag </b> closes nothing: no tag it closes is open before it.");
    }

    [Theory]
    [InlineData("renpy", "Wait{w} now{nw}", "{w}Подожди сейчас{nw}")]
    [InlineData("renpy", "{b}Start", "Начало {b}")]
    [InlineData("unity", "Line<br/>two", "Строка<br/>два")]
    [InlineData("unity", "<sprite=1> Coins", "Монеты <sprite=1>")]
    [InlineData("godot", "[b]Bold[/b] [i]it[/i]", "[i]к[/i] [b]Ж[/b]")]
    public void Tags_the_original_doesnt_pair_and_moved_pairs_are_accepted(string tool, string original, string translation)
    {
        // Act
        var issues = Validate(tool, original, translation);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Translation_over_the_limit_is_an_error()
    {
        // Act
        var issues = Validate("rpgmaker", "Potion", "Зелье!", maxLength: 5);

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Error, "too-long", "The translation has 6 characters; the game allows at most 5."));
    }

    [Fact]
    public void Display_width_limit_names_the_cells()
    {
        // Act
        var issues = Validate("rpgmaker", "Sword", "大剣士", maxLength: 5, unit: LengthUnit.DisplayWidth);

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Error, "too-long",
            "The translation is 6 cells wide (wide characters such as CJK and emoji count 2); the game allows at most 5."));
    }

    [Theory]
    [InlineData("Йо́жик", LengthUnit.Characters, 5)]
    [InlineData("Йо́жик", LengthUnit.DisplayWidth, 5)]
    [InlineData("大剣士", LengthUnit.Characters, 3)]
    [InlineData("大剣士", LengthUnit.DisplayWidth, 6)]
    [InlineData("ｶﾀｶﾅ", LengthUnit.DisplayWidth, 4)]
    [InlineData("ＡＢ", LengthUnit.DisplayWidth, 4)]
    [InlineData("한국어", LengthUnit.DisplayWidth, 6)]
    [InlineData("👨‍👩‍👧", LengthUnit.Characters, 1)]
    [InlineData("👨‍👩‍👧", LengthUnit.DisplayWidth, 2)]
    [InlineData("❤️", LengthUnit.DisplayWidth, 2)]
    [InlineData("🇫🇷", LengthUnit.DisplayWidth, 2)]
    [InlineData("a\r\nb\nc", LengthUnit.Characters, 3)]
    [InlineData("a‍b", LengthUnit.Characters, 2)]
    public void Length_counts_what_the_reader_sees(string text, LengthUnit unit, int expected)
    {
        // Act
        var length = TextLength.Measure(text, unit);

        // Assert
        length.Should().Be(expected);
    }

    [Fact]
    public void Limit_leaves_markup_out()
    {
        // Act
        var issues = Validate("rpgmaker", @"\C[2]Potion\C[0]", @"\C[2]Зелье\C[0]", maxLength: 5);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Same_text_as_the_original_is_a_warning()
    {
        // Act
        var issues = Validate("rpgmaker", "OK", "OK");

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Warning, "untranslated"));
    }

    [Theory]
    [InlineData("unity", "{0}/{1}")]
    [InlineData("rpgmaker", "???")]
    public void Same_text_without_letters_is_not_untranslated(string tool, string original)
    {
        // Act
        var issues = Validate(tool, original, original);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Different_leading_whitespace_is_a_warning()
    {
        // Act
        var issues = Validate("rpgmaker", " attacks!", "атакует!");

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Warning, "whitespace"));
    }

    [Fact]
    public void Different_line_break_count_is_a_warning()
    {
        // Act
        var issues = Validate("rpgmaker", "A young knight\nfrom the north.", "Юный рыцарь с севера.");

        // Assert
        issues.Should().Equal(new StringIssue(StringIssueSeverity.Warning, "line-breaks", "The original has 1 line break(s), the translation 0."));
    }

    [Fact]
    public void Without_markup_placeholders_of_the_original_are_still_required()
    {
        // Arrange
        var source = new ExtractedString("k", "Hi @name", null, null, ["@name"]);

        // Act
        var issues = StringValidator.Validate(source, "Привет", TextMarkup.None);

        // Assert
        issues.Select(i => i.Rule).Should().Equal("placeholder-missing");
    }

    [Theory]
    [InlineData("renpy", "{/b}Текст")]
    [InlineData("godot", "[b]Ж[i]к[/b][/i]")]
    [InlineData("unreal", "Выход</>")]
    public void Alone_reports_tags_closing_nothing_or_crossed_as_warnings(string tool, string translation)
    {
        // Act
        var issues = StringValidator.ValidateAlone(translation, Markup(tool));

        // Assert
        issues.Select(i => (i.Severity, i.Rule)).Should().Equal((StringIssueSeverity.Warning, "tag-unbalanced"));
    }

    [Theory]
    [InlineData("renpy", "{b}Текст{/b} и {w}пауза")]
    [InlineData("renpy", "{b}Начало без конца")]
    [InlineData("rpgmaker", @"\C[2]Цвет")]
    public void Alone_accepts_balanced_and_unclosed_tags(string tool, string translation)
    {
        // Act
        var issues = StringValidator.ValidateAlone(translation, Markup(tool));

        // Assert
        issues.Should().BeEmpty();
    }
}
