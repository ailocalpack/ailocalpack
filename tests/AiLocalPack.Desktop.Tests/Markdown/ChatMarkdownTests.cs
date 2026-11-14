using AiLocalPack.Desktop.Markdown;

namespace AiLocalPack.Desktop.Tests.Markdown;

/// <summary>The Markdown of the agents' chat replies parsed into blocks and inlines.</summary>
public sealed class ChatMarkdownTests
{
    private static void ShouldBe(IReadOnlyList<MdBlock> actual, params MdBlock[] expected) =>
        actual.Should().BeEquivalentTo(expected, o => o.PreferringRuntimeMemberTypes().ComparingByValue<MdRule>().ComparingByValue<MdLineBreak>().WithStrictOrdering());

    private static MdParagraph P(params MdInline[] inlines) => new(inlines);

    private static MdText T(string text) => new(text);

    [Fact]
    public void Plain_text_is_one_paragraph()
    {
        // Act
        var blocks = ChatMarkdown.Parse("Done, 12 strings changed.");

        // Assert
        ShouldBe(blocks, P(T("Done, 12 strings changed.")));
    }

    [Fact]
    public void Empty_text_has_no_blocks()
    {
        // Act
        var blocks = ChatMarkdown.Parse(string.Empty);

        // Assert
        blocks.Should().BeEmpty();
    }

    [Fact]
    public void Blank_lines_split_paragraphs_and_single_line_breaks_stay()
    {
        // Act
        var blocks = ChatMarkdown.Parse("One\r\ntwo\r\n\r\nThree");

        // Assert
        ShouldBe(blocks, P(T("One"), new MdLineBreak(), T("two")), P(T("Three")));
    }

    [Fact]
    public void Headings_keep_their_level()
    {
        // Act
        var blocks = ChatMarkdown.Parse("# Title\n### Sub ###\n#hashtag");

        // Assert
        ShouldBe(blocks, new MdHeading(1, [T("Title")]), new MdHeading(3, [T("Sub")]), P(T("#hashtag")));
    }

    [Fact]
    public void Bold_italic_strike_and_code_are_inlines()
    {
        // Act
        var inlines = ChatMarkdown.Inlines("**Bold** *it* _also_ ~~old~~ `ru.json` ***both***");

        // Assert
        inlines.Should().BeEquivalentTo(
            new MdInline[]
            {
                new MdStrong([T("Bold")]), T(" "), new MdEmphasis([T("it")]), T(" "), new MdEmphasis([T("also")]), T(" "),
                new MdStrike([T("old")]), T(" "), new MdCode("ru.json"), T(" "), new MdStrong([new MdEmphasis([T("both")])]),
            },
            o => o.PreferringRuntimeMemberTypes().ComparingByValue<MdRule>().ComparingByValue<MdLineBreak>().WithStrictOrdering());
    }

    [Fact]
    public void Nested_bold_inside_italic_parses()
    {
        // Act
        var inlines = ChatMarkdown.Inlines("*a **b** c*");

        // Assert
        inlines.Should().BeEquivalentTo(
            new MdInline[] { new MdEmphasis([T("a "), new MdStrong([T("b")]), T(" c")]) },
            o => o.PreferringRuntimeMemberTypes().ComparingByValue<MdRule>().ComparingByValue<MdLineBreak>().WithStrictOrdering());
    }

    [Fact]
    public void Underscores_inside_words_and_lone_stars_stay_text()
    {
        // Act
        var inlines = ChatMarkdown.Inlines("snake_case_key and 2 * 3 and **open");

        // Assert
        inlines.Should().BeEquivalentTo(new MdInline[] { T("snake_case_key and 2 * 3 and **open") }, o => o.PreferringRuntimeMemberTypes());
    }

    [Fact]
    public void Markers_inside_code_are_text()
    {
        // Act
        var inlines = ChatMarkdown.Inlines("`**not bold**`");

        // Assert
        inlines.Should().BeEquivalentTo(new MdInline[] { new MdCode("**not bold**") }, o => o.PreferringRuntimeMemberTypes());
    }

    [Fact]
    public void Escaped_markers_are_text()
    {
        // Act
        var inlines = ChatMarkdown.Inlines(@"\*not italic\*");

        // Assert
        inlines.Should().BeEquivalentTo(new MdInline[] { T("*not italic*") }, o => o.PreferringRuntimeMemberTypes());
    }

    [Fact]
    public void Links_and_bare_urls_open_web_addresses()
    {
        // Act
        var inlines = ChatMarkdown.Inlines("See [the wiki](https://example.org/w) or https://example.org/a_(b).");

        // Assert
        inlines.Should().BeEquivalentTo(
            new MdInline[]
            {
                T("See "), new MdLink([T("the wiki")], new Uri("https://example.org/w")), T(" or "),
                new MdLink([T("https://example.org/a_(b)")], new Uri("https://example.org/a_(b)")), T("."),
            },
            o => o.PreferringRuntimeMemberTypes().ComparingByValue<MdRule>().ComparingByValue<MdLineBreak>().WithStrictOrdering());
    }

    [Fact]
    public void A_link_to_a_file_or_script_never_opens()
    {
        // Act
        var inlines = ChatMarkdown.Inlines("[x](file:///etc/passwd) [y](javascript:alert(1))");

        // Assert
        inlines.OfType<MdLink>().Select(l => l.Url).Should().Equal(new Uri?[] { null, null });
    }

    [Fact]
    public void Bullet_list_items_are_blocks()
    {
        // Act
        var blocks = ChatMarkdown.Parse("Changed:\n- names\n- **menus**");

        // Assert
        ShouldBe(blocks, P(T("Changed:")), new MdList(false, 1, [[P(T("names"))], [P(new MdStrong([T("menus")]))]]));
    }

    [Fact]
    public void Numbered_list_keeps_its_start()
    {
        // Act
        var blocks = ChatMarkdown.Parse("3. three\n4. four");

        // Assert
        ShouldBe(blocks, new MdList(true, 3, [[P(T("three"))], [P(T("four"))]]));
    }

    [Fact]
    public void Indented_items_nest()
    {
        // Act
        var blocks = ChatMarkdown.Parse("1. Menus\n  - Start\n  - Quit\n2. Items");

        // Assert
        ShouldBe(blocks, new MdList(true, 1,
        [
            [P(T("Menus")), new MdList(false, 1, [[P(T("Start"))], [P(T("Quit"))]])],
            [P(T("Items"))],
        ]));
    }

    [Fact]
    public void A_loose_list_continues_after_blank_lines()
    {
        // Act
        var blocks = ChatMarkdown.Parse("- a\n\n- b\n\nAfter");

        // Assert
        ShouldBe(blocks, new MdList(false, 1, [[P(T("a"))], [P(T("b"))]]), P(T("After")));
    }

    [Fact]
    public void Fenced_code_keeps_its_text_and_language()
    {
        // Act
        var blocks = ChatMarkdown.Parse("```json\n{\n  \"a\": \"**b**\"\n}\n```\nAfter");

        // Assert
        ShouldBe(blocks, new MdCodeBlock("json", "{\n  \"a\": \"**b**\"\n}"), P(T("After")));
    }

    [Fact]
    public void An_unclosed_fence_runs_to_the_end()
    {
        // Act
        var blocks = ChatMarkdown.Parse("```\nline 1\nline 2");

        // Assert
        ShouldBe(blocks, new MdCodeBlock(string.Empty, "line 1\nline 2"));
    }

    [Fact]
    public void Quotes_hold_blocks()
    {
        // Act
        var blocks = ChatMarkdown.Parse("> Original: *Hello*\n> - one");

        // Assert
        ShouldBe(blocks, new MdQuote([P(T("Original: "), new MdEmphasis([T("Hello")])), new MdList(false, 1, [[P(T("one"))]])]));
    }

    [Fact]
    public void A_rule_splits_blocks()
    {
        // Act
        var blocks = ChatMarkdown.Parse("a\n---\nb");

        // Assert
        ShouldBe(blocks, P(T("a")), new MdRule(), P(T("b")));
    }

    [Fact]
    public void Tables_have_header_alignment_and_rows()
    {
        // Act
        var blocks = ChatMarkdown.Parse("| Key | Was | Now |\n|:--|:-:|--:|\n| `a` | Hi | Привет |\n| b \\| c | Bye |");

        // Assert
        ShouldBe(blocks, new MdTable(
            [[T("Key")], [T("Was")], [T("Now")]],
            [MdAlign.Left, MdAlign.Center, MdAlign.Right],
            [
                [[new MdCode("a")], [T("Hi")], [T("Привет")]],
                [[T("b | c")], [T("Bye")], []],
            ]));
    }

    [Fact]
    public void Raw_html_stays_text()
    {
        // Act
        var blocks = ChatMarkdown.Parse("<b>hi</b>");

        // Assert
        ShouldBe(blocks, P(T("<b>hi</b>")));
    }

    [Fact]
    public void Plain_text_drops_the_markers()
    {
        // Arrange
        var blocks = ChatMarkdown.Parse("## Done\nChanged **12** strings:\n- `menu.start`\n- [names](https://example.org)");

        // Act
        var text = ChatMarkdown.PlainText(blocks);

        // Assert
        text.Should().Be("Done\nChanged 12 strings:\n• menu.start\n• names");
    }
}
