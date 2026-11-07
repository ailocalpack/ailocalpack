using AiLocalPack.Core.Fonts;

namespace AiLocalPack.Core.Tests.Fonts;

public sealed class FontWhitelistTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void The_bundled_list_has_noto_sans_as_its_one_default()
    {
        // Act
        var defaults = FontWhitelist.Bundled.Fonts.Where(f => f.IsDefault).Select(f => f.Id);

        // Assert
        defaults.Should().Equal("noto-sans");
    }

    [Fact]
    public void Latin_cyrillic_greek_arabic_and_thai_fonts_ship_inside_the_app_and_cjk_ones_are_downloaded()
    {
        // Act
        var bundled = FontWhitelist.Bundled.Fonts.ToDictionary(f => f.Id, f => f.IsBundled);

        // Assert
        bundled.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["noto-sans"] = true,
            ["noto-sans-arabic"] = true,
            ["noto-sans-thai"] = true,
            ["noto-sans-jp"] = false,
            ["noto-sans-kr"] = false,
            ["noto-sans-sc"] = false,
            ["noto-sans-tc"] = false,
        });
    }

    [Fact]
    public void Downloaded_fonts_come_from_a_pinned_commit_of_the_noto_repository()
    {
        // Act
        var urls = FontWhitelist.Bundled.Fonts.SelectMany(f => f.Files).Where(f => !f.IsBundled).Select(f => f.Url!.ToString());

        // Assert
        urls.Should().AllSatisfy(url => url.Should().MatchRegex("^https://raw\\.githubusercontent\\.com/notofonts/noto-cjk/[0-9a-f]{40}/"));
    }

    [Theory]
    [InlineData("ja", new[] { "noto-sans-jp", "noto-sans" })]
    [InlineData("zh-Hant", new[] { "noto-sans-tc", "noto-sans" })]
    [InlineData("th", new[] { "noto-sans-thai", "noto-sans" })]
    [InlineData("ru", new[] { "noto-sans" })]
    [InlineData("vi", new[] { "noto-sans" })]
    public void Fonts_for_a_language_put_its_own_fonts_before_the_default(string language, string[] expected)
    {
        // Act
        var fonts = FontWhitelist.Bundled.For(language).Select(f => f.Id);

        // Assert
        fonts.Should().Equal(expected);
    }

    [Fact]
    public void Parse_reads_files_by_face_with_their_hash_and_address()
    {
        // Arrange
        var json = Whitelist("""{"id": "cjk", "name": "CJK", "license": "OFL-1.1", "languages": ["ja"], "files": {"bold": {"file": "B.otf", "size": 2, "sha256": "HASH", "url": "https://example.org/B.otf"}, "regular": {"file": "R.otf", "size": 1, "sha256": "HASH", "url": "https://example.org/R.otf"}}}""");

        // Act
        var font = FontWhitelist.Parse(json).Find("cjk")!;

        // Assert
        font.Files.Should().BeEquivalentTo(
            [
                new WhitelistFontFile(FontFace.Regular, "R.otf", 1, Hash, new Uri("https://example.org/R.otf")),
                new WhitelistFontFile(FontFace.Bold, "B.otf", 2, Hash, new Uri("https://example.org/B.otf")),
            ],
            o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("""{"id": "x", "name": "X", "license": "Apache-2.0", "files": {"regular": {"file": "X.ttf", "size": 1, "sha256": "HASH"}}}""", "only OFL-1.1")]
    [InlineData("""{"id": "x", "name": "X", "license": "OFL-1.1", "files": {"regular": {"file": "X.ttf", "size": 1, "sha256": "HASH", "url": "http://example.org/X.ttf"}}}""", "not https")]
    [InlineData("""{"id": "x", "name": "X", "license": "OFL-1.1", "languages": ["xx"], "files": {"regular": {"file": "X.ttf", "size": 1, "sha256": "HASH"}}}""", "unknown language")]
    [InlineData("""{"id": "x", "name": "X", "license": "OFL-1.1", "files": {"regular": {"file": "../X.ttf", "size": 1, "sha256": "HASH"}}}""", "invalid file name")]
    [InlineData("""{"id": "x", "name": "X", "license": "OFL-1.1", "files": {"bold": {"file": "X.ttf", "size": 1, "sha256": "HASH"}}}""", "no regular file")]
    [InlineData("""{"id": "X!", "name": "X", "license": "OFL-1.1", "files": {"regular": {"file": "X.ttf", "size": 1, "sha256": "HASH"}}}""", "Invalid font ID")]
    [InlineData("""{"id": "noto", "name": "X", "license": "OFL-1.1", "files": {"regular": {"file": "Y.ttf", "size": 1, "sha256": "HASH"}}}""", "listed twice")]
    public void Parse_refuses_an_invalid_font(string font, string message)
    {
        // Arrange
        var json = Whitelist(font);

        // Act
        var act = () => FontWhitelist.Parse(json);

        // Assert
        act.Should().Throw<FontWhitelistException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Parse_refuses_a_list_without_a_default_font()
    {
        // Arrange
        var json = """{"schemaVersion": 1, "fonts": [{"id": "x", "name": "X", "license": "OFL-1.1", "files": {"regular": {"file": "X.ttf", "size": 1, "sha256": "HASH"}}}]}""".Replace("HASH", Hash, StringComparison.Ordinal);

        // Act
        var act = () => FontWhitelist.Parse(json);

        // Assert
        act.Should().Throw<FontWhitelistException>().WithMessage("*default*");
    }

    /// <summary>A whitelist with a default font "noto" plus <paramref name="font"/>.</summary>
    private static string Whitelist(string font) => """
        {"schemaVersion": 1, "fonts": [
          {"id": "noto", "name": "Noto", "license": "OFL-1.1", "default": true, "files": {"regular": {"file": "N.ttf", "size": 1, "sha256": "HASH"}}},
          FONT
        ]}
        """.Replace("FONT", font, StringComparison.Ordinal).Replace("HASH", Hash, StringComparison.Ordinal);
}
