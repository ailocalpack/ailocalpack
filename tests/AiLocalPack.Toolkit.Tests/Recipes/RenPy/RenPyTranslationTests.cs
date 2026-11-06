using AiLocalPack.Toolkit.Recipes.RenPy;

namespace AiLocalPack.Toolkit.Tests.Recipes.RenPy;

/// <summary>Ren'Py's dialogue identifiers and string quoting, against what Ren'Py itself writes.</summary>
public sealed class RenPyTranslationTests
{
    private static RenPySay Say(string? who, string what, IReadOnlyList<string>? attributes = null, bool interact = true, string? with = null,
        IReadOnlyList<string>? temporary = null, string? id = null, string? arguments = null) =>
        new(who, attributes, temporary, what, interact, id, arguments, with);

    /// <summary>
    /// Golden identifiers from <c>the_question/game/tl/russian/script.rpy</c> in Ren'Py's repository (written by
    /// Ren'Py's <c>Generate Translations</c>), with the label and the statement each one translates.
    /// </summary>
    public static TheoryData<string, string?, string, bool, string> TheQuestion => new()
    {
        { "start", null, "It's only when I hear the sounds of shuffling feet and supplies being put away that I realize that the lecture's over.", true, "start_915cb944" },
        { "start", null, "Professor Eileen's lectures are usually interesting, but today I just couldn't concentrate on it.", true, "start_d48fb984" },
        { "rightaway", "s", "Hi there! How was class?", true, "rightaway_cf214f74" },
        { "rightaway", "m", "Good...", true, "rightaway_f5f51c33" },
        { "rightaway", "s", "Sure, but what's a \"visual novel?\"", false, "rightaway_8cb5deeb" },
        { "book", "s", "So where does the \"visual\" part come in?", true, "book_1c7ac61a" },
        { "marry", null, "{b}Good Ending{/b}.", true, "marry_2ad49007" },
        { "later", null, "{b}Bad Ending{/b}.", true, "later_f5219ac2" },
    };

    [Theory]
    [MemberData(nameof(TheQuestion))]
    public void Identifier_matches_the_one_Renpy_generates(string label, string? who, string what, bool interact, string expected)
    {
        // Arrange
        var code = RenPyTranslation.SayCode(Say(who, what, interact: interact));

        // Act
        var identifier = RenPyTranslation.Identifier(label, RenPyTranslation.Digest([code]), _ => false);

        // Assert
        identifier.Should().Be(expected);
    }

    [Fact]
    public void Identifier_takes_the_first_free_suffix()
    {
        // Arrange
        var taken = new HashSet<string> { "start_915cb944", "start_915cb944_1" };

        // Act
        var identifier = RenPyTranslation.Identifier("start", "915cb944", taken.Contains);

        // Assert
        identifier.Should().Be("start_915cb944_2");
    }

    [Fact]
    public void Identifier_before_any_label_is_the_digest_and_dots_of_local_labels_become_underscores()
    {
        // Arrange
        var digest = "915cb944";

        // Act
        var ids = (RenPyTranslation.Identifier(null, digest, _ => false), RenPyTranslation.Identifier("chapter.local", digest, _ => false));

        // Assert
        ids.Should().Be(("915cb944", "chapter_local_915cb944"));
    }

    [Fact]
    public void Digest_of_a_block_hashes_each_statement_with_a_line_break()
    {
        // Arrange
        string[] block = ["voice \"a.ogg\"", "s \"Hi\""];

        // Act
        var digest = RenPyTranslation.Digest(block);

        // Assert (MD5 of 'voice "a.ogg"\r\ns "Hi"\r\n')
        digest.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData("voice \"a.ogg\"\r\ns \"Hi\"\r\n"u8))[..8]);
    }

    [Fact]
    public void Say_code_lists_speaker_attributes_text_and_clauses_in_Renpy_order()
    {
        // Arrange
        var say = Say("e", "Hi", ["happy"], interact: false, with: "dissolve", temporary: ["wave"], id: "greet", arguments: "(slow=True)");

        // Act
        var code = RenPyTranslation.SayCode(say);

        // Assert
        code.Should().Be("e happy @ wave \"Hi\" nointeract id greet (slow=True) with dissolve");
    }

    [Fact]
    public void Say_code_with_a_translation_leaves_out_the_id_clause()
    {
        // Arrange
        var say = Say("e", "Hi", id: "greet");

        // Act
        var code = RenPyTranslation.SayCode(say, "Привет", withIdentifier: false);

        // Assert
        code.Should().Be("e \"Привет\"");
    }

    [Fact]
    public void Say_string_escapes_backslashes_quotes_line_breaks_and_runs_of_spaces()
    {
        // Arrange
        var text = "a \\ \"b\"\nc   d";

        // Act
        var encoded = RenPyTranslation.EncodeSayString(text);

        // Assert
        encoded.Should().Be("\"a \\\\ \\\"b\\\"\\nc \\ \\ d\"");
    }

    [Fact]
    public void Placeholders_are_text_tags_interpolations_their_escapes_and_percent_fields()
    {
        // Arrange
        var text = "{b}Hi{/b} [player]! {{not a tag}} [[x] 100%% %(gold)d {w=0.5}";

        // Act
        var placeholders = RenPyTranslation.Placeholders(text);

        // Assert
        placeholders.Should().Equal("{b}", "{/b}", "[player]", "{{", "[[", "%%", "%(gold)d", "{w=0.5}");
    }

    [Fact]
    public void Translatable_calls_are_found_in_every_quoting_and_decoded()
    {
        // Arrange
        var code = "textbutton _(\"Start\")\nx = __('It\\'s') + _(u\"\"\"Line\nbreak\"\"\")\ny = f(\"_(not)\") + my_(\"no\") + _( \"\\u00e9t\\u00e9\" )";

        // Act
        var texts = RenPyTranslation.TranslatableCalls(code).ToList();

        // Assert
        texts.Should().Equal("Start", "It's", "Line\nbreak", "été");
    }

    [Fact]
    public void Language_name_is_prefixed_and_a_valid_Renpy_name()
    {
        // Arrange
        var tag = "zh-Hans";

        // Act
        var name = RenPyTranslation.LanguageName(tag);

        // Assert
        name.Should().Be("ailocalpack_zh_Hans");
    }
}
