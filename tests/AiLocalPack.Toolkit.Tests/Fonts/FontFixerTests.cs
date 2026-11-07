using System.Security.Cryptography;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Registry;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RpgMaker;

namespace AiLocalPack.Toolkit.Tests.Fonts;

public sealed class FontFixerTests : IDisposable
{
    private const string JapaneseUrl = "https://fonts.example.org/TestJP-Regular.ttf";
    private const string Title = "data/System.json:/gameTitle";

    /// <summary>A stand-in for Noto Sans JP: ASCII and the kana the tests use.</summary>
    private static readonly byte[] JapaneseFont = FontWriter.TrueType("Test JP", [.. FontWriter.Ascii, .. "ゆうしゃのクエスト".Select(c => (int)c)]);

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _http = new();
    private readonly RpgMakerRecipe _recipe = new();

    public FontFixerTests() => _dir.RpgMakerGame("game");

    public void Dispose()
    {
        _dir.Dispose();
        _http.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    /// <summary>The bundled Noto Sans (the default) and a downloaded Japanese font.</summary>
    private static FontWhitelist Whitelist() => new([
        FontWhitelist.Bundled.Find("noto-sans")!,
        new WhitelistFont("test-jp", "Test JP", "OFL-1.1", ["ja"], false, [
            new WhitelistFontFile(FontFace.Regular, "TestJP-Regular.ttf", JapaneseFont.Length, Convert.ToHexStringLower(SHA256.HashData(JapaneseFont)), new Uri(JapaneseUrl)),
        ]),
    ]);

    private FontFixer Fixer(FontWhitelist? whitelist = null) => new(
        whitelist ?? Whitelist(),
        new FontStore(_dir.Combine("cache"), _http.CreateClient(), options: new PackFetchOptions(TimeSpan.FromSeconds(5), Attempts: 1)));

    private Task<FontFixResult> Apply(string language, string title, IReadOnlyList<string>? preferred = null, FontWhitelist? whitelist = null) =>
        Fixer(whitelist).ApplyAsync(_recipe, Game, language, new Dictionary<string, string> { [Title] = title }, preferred ?? [], new DirectoryRebuildOutput(Out), cancellationToken: Ct);

    [Fact]
    public void Check_lists_the_letters_the_game_font_lacks()
    {
        // Act
        var check = FontFixer.Check(_recipe, Game, ["Зелье 2"])!;

        // Assert
        (check.Fonts.Single().Family, check.Fonts.Single().MissingLetters).Should().BeEquivalentTo(
            ("Fixture Sans", "Зелье".EnumerateRunes().Select(r => r.Value).Distinct().Order()));
    }

    [Fact]
    public void Placeholders_need_no_glyphs()
    {
        // Act
        var check = FontFixer.Check(_recipe, Game, ["\\C[2]\\PIC<Ящик>"])!;

        // Assert
        (check.Letters.Count, check.Symbols.Count).Should().Be((0, 0));
    }

    [Fact]
    public void Characters_the_game_text_already_uses_need_no_swap()
    {
        // Arrange
        // An ASCII-only font, while the original text has "Café au lait".
        File.WriteAllBytes(_dir.Combine("game/fonts/fixture-sans.woff"), FontWriter.Woff(FontWriter.TrueType("Ascii", FontWriter.Ascii)));

        // Act
        var check = FontFixer.Check(_recipe, Game, ["Café noir"])!;

        // Assert
        (check.NeedsSwap, check.Letters.Count).Should().Be((false, 0));
    }

    [Fact]
    public void A_missing_symbol_alone_needs_no_swap()
    {
        // Act
        var check = FontFixer.Check(_recipe, Game, ["Café — ouvert"])!;

        // Assert
        (check.NeedsSwap, check.Fonts.Single().MissingSymbols).Should().BeEquivalentTo((false, new[] { 0x2014 }));
    }

    [Fact]
    public void A_font_that_cant_be_read_is_left_alone()
    {
        // Arrange
        File.WriteAllText(_dir.Combine("game/fonts/fixture-sans.woff"), "broken");

        // Act
        var check = FontFixer.Check(_recipe, Game, ["Зелье"])!;

        // Assert
        (check.NeedsSwap, check.Fonts.Single().Problem is not null).Should().Be((false, true));
    }

    [Fact]
    public async Task Russian_swaps_in_the_bundled_noto_sans()
    {
        // Act
        var result = await Apply("ru", "Квест");

        // Assert
        (result.Replacement?.Id, result.ManifestFonts, result.Files.Files, _http.Requests.Count).Should().BeEquivalentTo((
            "noto-sans",
            new[] { "noto-sans" },
            new[] { "data/System.json", "fonts/ailocalpack-NotoSans-Regular.ttf" },
            0));
    }

    [Fact]
    public async Task Fonts_with_every_letter_are_left_alone()
    {
        // Act
        var result = await Apply("fr", "Le Café");

        // Assert
        (result.Check!.NeedsSwap, result.Replacement, result.Files.Files, Directory.Exists(Out)).Should().BeEquivalentTo((false, (WhitelistFont?)null, Array.Empty<string>(), false));
    }

    [Fact]
    public async Task Japanese_downloads_the_font_made_for_it()
    {
        // Arrange
        _http.OnGet(JapaneseUrl, JapaneseFont);

        // Act
        var result = await Apply("ja", "ゆうしゃのクエスト");

        // Assert
        (result.Replacement?.Id, result.Uncovered.Count, File.ReadAllBytes(_dir.Combine("out/fonts/ailocalpack-TestJP-Regular.ttf")))
            .Should().BeEquivalentTo(("test-jp", 0, JapaneseFont));
    }

    [Fact]
    public async Task A_needed_font_that_cant_be_downloaded_fails_and_writes_nothing()
    {
        // Arrange
        _http.On(HttpMethod.Get, JapaneseUrl, _ => throw new HttpRequestException("No route to host"));

        // Act
        var result = await Apply("ja", "ゆうしゃのクエスト");

        // Assert
        (result.Failed, result.FailedFont?.Id, result.Failure?.Problem, result.Files.Files.Count, Directory.Exists(Out))
            .Should().Be((true, "test-jp", FontFetchProblem.Offline, 0, false));
    }

    [Fact]
    public async Task Without_a_font_that_has_every_letter_the_closest_is_used_and_the_rest_reported()
    {
        // Act
        var result = await Apply("ru", "Квест ゆ", whitelist: new FontWhitelist([FontWhitelist.Bundled.Find("noto-sans")!]));

        // Assert
        (result.Replacement?.Id, result.Uncovered).Should().BeEquivalentTo(("noto-sans", new[] { (int)'ゆ' }));
    }

    [Fact]
    public async Task The_pack_fonts_are_tried_first()
    {
        // Arrange
        _http.OnGet(JapaneseUrl, JapaneseFont);

        // Act
        var result = await Apply("ru", "Квест", preferred: ["test-jp"]);

        // Assert
        (result.Replacement?.Id, _http.Requests.Count).Should().Be(("noto-sans", 1));
    }

    [Fact]
    public async Task A_pack_font_this_app_doesnt_know_is_ignored_and_reported()
    {
        // Act
        var result = await Apply("ru", "Квест", preferred: ["comic-sans"]);

        // Assert
        (result.Replacement?.Id, result.UnknownFonts).Should().BeEquivalentTo(("noto-sans", new[] { "comic-sans" }));
    }

    [Fact]
    public void Sample_shows_a_few_characters()
    {
        // Act
        var sample = FontFixer.Sample([.. "АБВГД".Select(c => (int)c)], 3);

        // Assert
        sample.Should().Be("А, Б, В, …");
    }
}
