using System.Text;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Unity;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AiLocalPack.Toolkit.Tests.Recipes.Unity;

public sealed class UnityRecipeTests : IDisposable
{
    private const string Assets = "Game_Data/resources.assets";
    private const string BundlePath = "Game_Data/StreamingAssets/text.bundle";

    private readonly TempDir _dir = new();
    private readonly UnityRecipe _recipe = new();

    private string Game => _dir.Combine("game");
    private string Output => _dir.Combine("output");
    private string Rebuilt => _dir.Combine("rebuilt");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(false, false, true, false, 5)]
    [InlineData(true, false, true, false, 5)]
    [InlineData(false, true, true, false, 5)]
    [InlineData(true, true, false, false, 3)]
    [InlineData(false, false, true, true, 5)]
    public void Rebuild_applies_every_translated_string(bool il2Cpp, bool mac, bool trees, bool bigEndian, int expected)
    {
        // Arrange
        UnityFixture.Create(Game, il2Cpp, mac, trees, bigEndian);
        var translations = TranslateAll(_recipe.Extract(Game, Ct));

        // Act
        var result = _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        result.Applied.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    public void Rebuilt_files_read_back_as_the_translations(bool il2Cpp, bool mac, bool trees, bool bigEndian)
    {
        // Arrange
        UnityFixture.Create(Game, il2Cpp, mac, trees, bigEndian);
        var translations = TranslateAll(_recipe.Extract(Game, Ct));
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);
        _dir.CopyFolder(Game, "rebuilt").CopyFolder(Output, "rebuilt");

        // Act
        var reread = _recipe.Extract(Rebuilt, Ct);

        // Assert
        reread.Strings.ToDictionary(s => s.Key, s => s.Original).Should().BeEquivalentTo(translations);
    }

    [Fact]
    public void Rebuild_keeps_identifiers_numbers_and_code_assets()
    {
        // Arrange
        UnityFixture.Create(Game);
        var translations = TranslateAll(_recipe.Extract(Game, Ct));

        // Act
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Output, Assets)))
            .Should().Contain("stable-id").And.Contain("1.00").And.Contain("keep-this-id").And.Contain("public class Example {}");
    }

    [Fact]
    public void Rebuild_never_writes_into_the_game_folder()
    {
        // Arrange
        UnityFixture.Create(Game);
        var before = File.ReadAllBytes(Path.Combine(Game, Assets));
        var translations = TranslateAll(_recipe.Extract(Game, Ct));

        // Act
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        File.ReadAllBytes(Path.Combine(Game, Assets)).Should().Equal(before);
    }

    [Fact]
    public void Rebuild_lists_the_changed_files()
    {
        // Arrange
        UnityFixture.Create(Game, macBundle: true);
        var extraction = _recipe.Extract(Game, Ct);

        // Act
        var result = _recipe.Rebuild(Game, "ru", TranslateAll(extraction), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        result.ChangedFiles.Should().Equal("Game.app/Contents/Resources/Data/resources.assets");
    }

    [Fact]
    public void Untranslated_or_empty_strings_change_nothing()
    {
        // Arrange
        UnityFixture.Create(Game);
        var extraction = _recipe.Extract(Game, Ct);
        var translations = extraction.Strings.ToDictionary(s => s.Key, s => s.Original);
        translations[extraction.Strings[0].Key] = "";

        // Act
        var result = _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        (result.Applied, result.ChangedFiles.Count, Directory.Exists(Output)).Should().Be((0, 0, false));
    }

    [Fact]
    public void Unknown_keys_are_reported()
    {
        // Arrange
        UnityFixture.Create(Game);
        var translations = new Dictionary<string, string> { ["absent"] = "ignored" };

        // Act
        var result = _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        result.UnknownKeys.Should().Equal("absent");
    }

    [Fact]
    public void Extracted_keys_are_in_ordinal_order()
    {
        // Arrange
        UnityFixture.Create(Game);

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Select(s => s.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Keys_name_the_file_object_and_field()
    {
        // Arrange
        UnityFixture.Create(Game);

        // Act
        var keys = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key);

        // Assert
        keys.Should().Equal(
            "Game_Data/resources.assets:1/script/line/0",
            "Game_Data/resources.assets:1/script/line/2",
            "Game_Data/resources.assets:2/script/text",
            "Game_Data/resources.assets:3/m_Localized",
            "Game_Data/resources.assets:3/m_Text");
    }

    [Fact]
    public void Touched_files_are_the_assets_holding_the_keys()
    {
        // Arrange
        UnityFixture.Create(Game);
        var extraction = _recipe.Extract(Game, Ct);

        // Act
        var touched = _recipe.TouchedFiles(Game, extraction.Strings.Select(s => s.Key));

        // Assert
        touched.Should().Equal(extraction.Files);
    }

    [Fact]
    public void Unknown_keys_touch_no_file()
    {
        // Arrange
        UnityFixture.Create(Game);

        // Act
        var touched = _recipe.TouchedFiles(Game, ["absent"]);

        // Assert
        touched.Should().BeEmpty();
    }

    [Fact]
    public void Variant_of_the_fixture_is_detected()
    {
        // Arrange
        UnityFixture.Create(Game, il2Cpp: true);

        // Act
        var detection = new EngineDetector().Detect(Game);

        // Assert
        detection.Kind.Variant.Should().Be(EngineVariants.Il2Cpp);
    }

    [Fact]
    public void Plain_text_lines_are_keyed_by_their_index()
    {
        // Arrange
        var text = Encoding.UTF8.GetBytes("﻿Hello\r\n\r\nBye\n");

        // Act
        var found = UnityText.Read("test.txt", text);

        // Assert
        found.Keys.Should().Equal("/line/0", "/line/2");
    }

    [Fact]
    public void Plain_text_replacement_keeps_the_BOM_and_line_endings()
    {
        // Arrange
        var text = Encoding.UTF8.GetBytes("﻿Hello\r\n\r\nBye\n");

        // Act
        var replaced = UnityText.Replace("test.txt", text, new Dictionary<string, string> { ["/line/0"] = "Привет" });

        // Assert
        Encoding.UTF8.GetString(replaced).Should().Be("﻿Привет\r\n\r\nBye\n");
    }

    [Fact]
    public void JSON_gives_only_its_text_fields()
    {
        // Arrange
        var json = Encoding.UTF8.GetBytes("{\"text\": \"Hello\", \"id\": \"keep\", \"n\":1.00}");

        // Act
        var found = UnityText.Read("dialog.json", json);

        // Assert
        found.Should().ContainSingle().Which.Key.Should().Be("/text");
    }

    [Fact]
    public void JSON_replacement_escapes_and_keeps_the_rest_of_the_syntax()
    {
        // Arrange
        var json = Encoding.UTF8.GetBytes("{\"text\": \"Hello\", \"id\": \"keep\", \"n\":1.00}");

        // Act
        var replaced = UnityText.Replace("dialog.json", json, new Dictionary<string, string> { ["/text"] = "a\"b\n" });

        // Assert
        Encoding.UTF8.GetString(replaced).Should().Be("{\"text\": \"a\\\"b\\n\", \"id\": \"keep\", \"n\":1.00}");
    }

    [Theory]
    [InlineData("code.cs", "public class Code {}")]
    [InlineData("binary", "a\0b")]
    [InlineData("data.txt", "<xml>data</xml>")]
    [InlineData("broken.json", "{not json")]
    public void Unsupported_TextAssets_are_left_alone(string name, string content)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(content);

        // Act
        var found = UnityText.Read(name, bytes);

        // Assert
        found.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_UTF8_is_not_translated()
    {
        // Arrange
        byte[] bytes = [0xFF, 0xFE];

        // Act
        var found = UnityText.Read("test", bytes);

        // Assert
        found.Should().BeEmpty();
    }

    [Fact]
    public void CSV_gives_the_English_cells_with_quotes_commas_and_newlines()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("KEY,English,Français\r\nwelcome,\"Hello, \"\"friend\"\"\",Bonjour\r\nbye,\"Good\nbye\",Au revoir\r\n");

        // Act
        var found = UnityText.Read("Localization", bytes);

        // Assert
        found.Should().BeEquivalentTo(new Dictionary<string, string> { ["/csv/welcome"] = "Hello, \"friend\"", ["/csv/bye"] = "Good\nbye" });
    }

    [Fact]
    public void CSV_replacement_changes_only_the_English_cell()
    {
        // Arrange
        const string source = "KEY,English,Français\r\nwelcome,\"Hello, \"\"friend\"\"\",Bonjour\r\nbye,\"Good\nbye\",Au revoir\r\n";

        // Act
        var replaced = UnityText.Replace("Localization", Encoding.UTF8.GetBytes(source), new Dictionary<string, string> { ["/csv/welcome"] = "Привет, \"друг\"" });

        // Assert
        Encoding.UTF8.GetString(replaced).Should().Be(source.Replace("\"Hello, \"\"friend\"\"\"", "\"Привет, \"\"друг\"\"\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("KEY,English\na,\"unterminated")]
    [InlineData("KEY,French\na,Bonjour")]
    public void Unsupported_CSV_never_falls_back_to_whole_lines(string source)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(source);

        // Act
        var found = UnityText.Read("Localization", bytes);

        // Assert
        found.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_CSV_keys_are_refused()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("KEY,English\na,One\na,Two\n");

        // Act
        var act = () => UnityText.Read("Localization", bytes);

        // Assert
        act.Should().Throw<InvalidDataException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void Markup_and_format_arguments_are_placeholders()
    {
        // Arrange
        const string text = "<b>Hello</b> {name} {0:N2} %s [ff0000]red[-]";

        // Act
        var placeholders = UnityText.Placeholders(text);

        // Assert
        placeholders.Should().Equal("<b>", "</b>", "{name}", "{0:N2}", "%s", "[ff0000]", "[-]");
    }

    [Fact]
    public void Corrupt_asset_is_a_recipe_error_with_its_path()
    {
        // Arrange
        UnityFixture.Create(Game);
        File.WriteAllBytes(Path.Combine(Game, Assets), [1, 2, 3]);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be(Assets);
    }

    [Fact]
    public void Invalid_TextAsset_length_is_a_recipe_error()
    {
        // Arrange
        UnityFixture.Create(Game);
        var path = Path.Combine(Game, Assets);
        long offset;
        var manager = new AssetsManager();
        try
        {
            var asset = manager.LoadAssetsFile(path, false);
            offset = asset.file.Metadata.AssetInfos[0].GetAbsoluteByteOffset(asset.file);
        }
        finally
        {
            manager.UnloadAll();
        }

        using (var stream = File.OpenWrite(path))
        {
            stream.Position = offset;
            stream.Write([0xFF, 0xFF, 0xFF, 0x7F]);
        }

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Invalid TextAsset string length*");
    }

    [Fact]
    public void Another_engine_is_refused()
    {
        // Arrange
        Directory.CreateDirectory(Game);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Not a Unity game*");
    }

    [Fact]
    public void Extraction_stops_when_cancelled()
    {
        // Arrange
        UnityFixture.Create(Game);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var act = () => _recipe.Extract(Game, cancelled.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Stripped_Mono_fields_are_recovered_from_the_assembly_metadata()
    {
        // Arrange
        UnityMonoFixture.Create(Game);

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Should().ContainSingle().Which.Original.Should().Be("Hello from stripped Mono!");
    }

    [Fact]
    public void Behaviour_whose_script_class_is_missing_is_left_out()
    {
        // Arrange
        UnityMonoFixture.Create(Game);

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Select(s => s.Key).Should().Equal("Game_Data/resources.assets:1/m_Text");
    }

    [Fact]
    public void Stripped_Mono_rebuild_reads_back_and_keeps_other_fields()
    {
        // Arrange
        UnityMonoFixture.Create(Game);
        var key = _recipe.Extract(Game, Ct).Strings[0].Key;
        _recipe.Rebuild(Game, "ru", new Dictionary<string, string> { [key] = "Привет из Mono!" }, new DirectoryRebuildOutput(Output), Ct);
        _dir.CopyFolder(Game, "rebuilt").CopyFolder(Output, "rebuilt");

        // Act
        var reread = _recipe.Extract(Rebuilt, Ct);

        // Assert
        (reread.Strings.Single().Original, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Output, Assets))).Contains("keep-id", StringComparison.Ordinal))
            .Should().Be(("Привет из Mono!", true));
    }

    [Theory]
    [InlineData(AssetBundleCompressionType.None)]
    [InlineData(AssetBundleCompressionType.LZ4)]
    [InlineData(AssetBundleCompressionType.LZMA)]
    public void Bundle_rebuild_reads_back_as_the_translations(AssetBundleCompressionType compression)
    {
        // Arrange
        CreateBundledGame(compression);
        var translations = TranslateAll(_recipe.Extract(Game, Ct));
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);
        _dir.CopyFolder(Game, "rebuilt").CopyFolder(Output, "rebuilt");

        // Act
        var reread = _recipe.Extract(Rebuilt, Ct);

        // Assert
        reread.Strings.ToDictionary(s => s.Key, s => s.Original).Should().BeEquivalentTo(translations);
    }

    [Fact]
    public void Bundle_is_the_touched_and_changed_file()
    {
        // Arrange
        CreateBundledGame(AssetBundleCompressionType.LZ4);
        var translations = TranslateAll(_recipe.Extract(Game, Ct));

        // Act
        var result = _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        (result.ChangedFiles.Single(), _recipe.TouchedFiles(Game, translations.Keys).Single()).Should().Be((BundlePath, BundlePath));
    }

    [Fact]
    public void Bundle_rebuild_keeps_opaque_resources()
    {
        // Arrange
        CreateBundledGame(AssetBundleCompressionType.LZMA);
        _recipe.Rebuild(Game, "ru", TranslateAll(_recipe.Extract(Game, Ct)), new DirectoryRebuildOutput(Output), Ct);
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(Path.Combine(Output, BundlePath), true);
            bundle.file.GetFileRange(1, out var offset, out var size);
            bundle.file.DataReader.Position = offset;

            // Act
            var resource = bundle.file.DataReader.ReadBytes(checked((int)size));

            // Assert
            resource.Should().Equal([1, 2, 3, 4, 5]);
        }
        finally
        {
            manager.UnloadAll();
        }
    }

    private static Dictionary<string, string> TranslateAll(RecipeExtraction extraction) =>
        extraction.Strings.ToDictionary(s => s.Key, s => "Перевод: " + s.Original, StringComparer.Ordinal);

    private void CreateBundledGame(AssetBundleCompressionType compression)
    {
        UnityFixture.Create(Game, il2Cpp: true, bundled: true);
        if (compression == AssetBundleCompressionType.None)
        {
            return;
        }

        var source = new AssetBundleFile();
        using var input = new AssetsFileReader(new MemoryStream(File.ReadAllBytes(Path.Combine(Game, BundlePath))));
        source.Read(input);
        using var packed = new MemoryStream();
        using var writer = new AssetsFileWriter(packed);
        source.Pack(writer, compression);
        File.WriteAllBytes(Path.Combine(Game, BundlePath), packed.ToArray());
    }
}
