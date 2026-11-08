using System.Buffers.Binary;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Unreal;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace AiLocalPack.Toolkit.Tests.Recipes.Unreal;

/// <summary><c>unreal</c> recipe on pak games: extract, rebuild into a mod pak, cultures (task 37).</summary>
public sealed class UnrealRecipeTests : IDisposable
{
    private const string Mac = "Sample.app/Contents/UE4/";
    private const string Mod = UnrealFixture.Paks + "/~mods/" + UnrealRecipe.ModName;
    private readonly TempDir _dir = new();
    private readonly UnrealRecipe _recipe = new();

    private string Game => _dir.Combine("game");

    private string Output => _dir.Combine("out");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, false)]
    [InlineData((byte)2, false)]
    [InlineData((byte)3, false)]
    [InlineData((byte)3, true)]
    public void Extract_gives_the_native_culture_texts(byte version, bool mac)
    {
        // Arrange
        UnrealFixture.Create(Game, version, mac);

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Select(s => s.Original).Should().BeEquivalentTo(UnrealFixture.Resource(version).Entries.Select(e => e.Text));
    }

    [Fact]
    public void Extract_keys_are_in_ordinal_order()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Select(s => s.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_names_the_pak_holding_the_localization(bool mac)
    {
        // Arrange
        UnrealFixture.Create(Game, macBundle: mac);

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Files.Should().Equal((mac ? Mac : "") + UnrealFixture.Pak);
    }

    [Fact]
    public void Extract_lists_text_arguments_and_rich_text_tags_as_placeholders()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var hint = _recipe.Extract(Game, Ct).Strings.Single(s => s.Original.StartsWith("<b>", StringComparison.Ordinal));

        // Assert
        hint.Placeholders.Should().Equal("<b>", "</>", "{0}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rebuild_writes_one_mod_pak(bool mac)
    {
        // Arrange
        UnrealFixture.Create(Game, macBundle: mac);
        var values = Translate("Play", "Играть");

        // Act
        var result = _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        (result.ChangedFiles, result.Applied).Should().BeEquivalentTo(((string[])[(mac ? Mac : "") + Mod], 1));
    }

    [Fact]
    public void Rebuild_leaves_the_game_paks_untouched()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var original = File.ReadAllBytes(Path.Combine(Game, UnrealFixture.Pak));

        // Act
        _recipe.Rebuild(Game, "ru", Translate("Play", "Играть"), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        File.ReadAllBytes(Path.Combine(Game, UnrealFixture.Pak)).Should().Equal(original);
    }

    [Fact]
    public void Rebuild_for_a_culture_the_game_lacks_replaces_the_native_texts()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var result = _recipe.Rebuild(Game, "ru", Translate("Play", "Играть"), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        var expected = UnrealFixture.Resource(3).Entries.Select(e => e.Key == "Play" ? e with { Text = "Играть" } : e);
        UnrealLocres.Read(ReadMod(result, UnrealFixture.English)).Entries.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Rebuild_for_a_culture_the_game_lacks_asks_for_the_native_language_in_game()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var result = _recipe.Rebuild(Game, "ru", Translate("Play", "Играть"), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        result.InGameLanguage.Should().Be("en");
    }

    [Fact]
    public void Rebuild_for_a_culture_the_game_ships_replaces_it_and_keeps_its_untranslated_entries()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var result = _recipe.Rebuild(Game, "fr", Translate("Play", "Jouer maintenant"), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        var expected = UnrealFixture.Resource(3, french: true).Entries.Select(e => e.Key == "Play" ? e with { Text = "Jouer maintenant" } : e);
        UnrealLocres.Read(ReadMod(result, UnrealFixture.French)).Entries.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Rebuild_for_a_culture_the_game_ships_needs_no_language_change_in_game()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var result = _recipe.Rebuild(Game, "fr", Translate("Play", "Jouer maintenant"), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        result.InGameLanguage.Should().BeNull();
    }

    [Fact]
    public void Rebuild_finds_the_game_regional_name_of_the_language()
    {
        // Arrange
        const string russian = "Sample/Content/Localization/Game/ru-RU/Game.locres";
        UnrealFixture.Create(Game);
        WritePak(new() { [UnrealFixture.English] = UnrealFixture.Resource(3).Write(), [russian] = UnrealFixture.Resource(3).Write(), [UnrealFixture.Meta] = UnrealFixture.Metadata() });

        // Act
        var result = _recipe.Rebuild(Game, "ru", Translate("Play", "Играть"), new DirectoryRebuildOutput(Output), Ct);

        // Assert
        UnrealLocres.Read(ReadMod(result, russian)).Entries.Single(e => e.Key == "Play").Text.Should().Be("Играть");
    }

    [Fact]
    public void Rebuild_refuses_a_stale_entry_of_the_game_culture()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var values = Translate("Play", "Jouer");
        var stale = UnrealFixture.Resource(3, french: true);
        stale = stale with { Entries = [.. stale.Entries.Select(e => e.Key == "Play" ? e with { SourceHash = 1 } : e)] };
        WritePak(new() { [UnrealFixture.English] = UnrealFixture.Resource(3).Write(), [UnrealFixture.French] = stale.Write() });

        // Act
        var act = () => _recipe.Rebuild(Game, "fr", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*missing or stale*");
    }

    [Fact]
    public void Rebuild_with_only_unknown_empty_or_unchanged_translations_writes_nothing()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var texts = _recipe.Extract(Game, Ct).Strings;
        var values = texts.ToDictionary(s => s.Key, s => s.Original);
        values[texts[0].Key] = "";
        values["unknown"] = "Unknown";

        // Act
        var result = _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        (result.Applied, result.ChangedFiles, result.UnknownKeys, Directory.Exists(Output)).Should().BeEquivalentTo((0, Array.Empty<string>(), (string[])["unknown"], false));
    }

    [Fact]
    public void Touched_files_are_the_paks_holding_the_keys()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var keys = Translate("Play", "Играть").Keys;

        // Act
        var touched = _recipe.TouchedFiles(Game, keys);

        // Assert
        touched.Should().Equal(UnrealFixture.Pak);
    }

    [Fact]
    public void Touched_files_of_unknown_keys_are_none()
    {
        // Arrange
        UnrealFixture.Create(Game);

        // Act
        var touched = _recipe.TouchedFiles(Game, ["unknown"]);

        // Assert
        touched.Should().BeEmpty();
    }

    [Fact]
    public void Extract_ignores_an_installed_mod_pak()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var before = _recipe.Extract(Game, Ct);
        _recipe.Rebuild(Game, "ru", Translate("Play", "Играть"), new DirectoryRebuildOutput(Output), Ct);
        _dir.CopyFolder(Output, "game");

        // Act
        var after = _recipe.Extract(Game, Ct);

        // Assert
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public void A_damaged_locres_names_the_resource()
    {
        // Arrange
        UnrealFixture.Create(Game);
        WritePak(new() { [UnrealFixture.English] = [1, 2, 3], [UnrealFixture.Meta] = UnrealFixture.Metadata() });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be(UnrealFixture.English);
    }

    [Fact]
    public void A_damaged_locmeta_names_the_metadata()
    {
        // Arrange
        UnrealFixture.Create(Game);
        WritePak(new() { [UnrealFixture.English] = UnrealFixture.Resource(3).Write(), [UnrealFixture.Meta] = [1, 2, 3] });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be(UnrealFixture.Meta);
    }

    [Fact]
    public void Engine_localization_is_not_game_text()
    {
        // Arrange
        UnrealFixture.Create(Game);
        WritePak(new() { ["Engine/Content/Localization/en/Engine.locres"] = UnrealFixture.Resource(3).Write() });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*No supported game localization*");
    }

    [Fact]
    public void A_target_without_its_native_culture_is_refused()
    {
        // Arrange
        UnrealFixture.Create(Game);
        WritePak(new() { [UnrealFixture.French] = UnrealFixture.Resource(3).Write(), [UnrealFixture.Meta] = UnrealFixture.Metadata() });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Native culture 'en'*");
    }

    [Fact]
    public void An_encrypted_pak_index_is_refused_for_lack_of_a_key()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var path = Path.Combine(Game, UnrealFixture.Pak);
        var bytes = File.ReadAllBytes(path);

        // The v4 footer adds one encrypted-index byte before the v3 footer.
        var encrypted = bytes[..^44].Concat(new byte[] { 1 }).Concat(bytes[^44..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(encrypted.AsSpan(encrypted.Length - 40), 4);
        File.WriteAllBytes(path, encrypted);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*AES*no key*");
    }

    [Fact]
    public void A_damaged_pak_names_the_archive()
    {
        // Arrange
        UnrealFixture.Create(Game);
        File.WriteAllBytes(Path.Combine(Game, UnrealFixture.Pak), [1, 2, 3]);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be(UnrealFixture.Pak);
    }

    [Fact]
    public void A_damaged_IoStore_index_next_to_the_pak_is_refused()
    {
        // Arrange
        UnrealFixture.Create(Game);
        _dir.File("game/" + UnrealFixture.Paks + "/pakchunk0.utoc", "utoc");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*IoStore TOC size*");
    }

    [Fact]
    public void A_folder_that_is_not_an_Unreal_game_is_refused()
    {
        // Arrange
        _dir.Dir("game");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*isn't an unambiguous Unreal*");
    }

    [Fact]
    public void Extract_stops_when_cancelled()
    {
        // Arrange
        UnrealFixture.Create(Game);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = () => _recipe.Extract(Game, cts.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_mod_pak_that_cannot_be_written_names_it()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var values = Translate("Play", "Играть");
        _dir.File("out", "not a directory");

        // Act
        var act = () => _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be(Mod);
    }

    private Dictionary<string, string> Translate(string original, string translation)
    {
        var key = _recipe.Extract(Game, Ct).Strings.Single(s => s.Original == original).Key;
        return new Dictionary<string, string> { [key] = translation };
    }

    private void WritePak(Dictionary<string, byte[]> files) => File.WriteAllBytes(Path.Combine(Game, UnrealFixture.Pak), UnrealPakWriter.Write(files));

    /// <summary>A file of the written mod pak, read through CUE4Parse.</summary>
    private byte[] ReadMod(RebuildResult result, string entry)
    {
        using var provider = new DefaultFileProvider(Path.GetDirectoryName(Path.Combine(Output, result.ChangedFiles.Single()))!, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_27));
        provider.Initialize();
        provider.Mount();
        return provider.Files[entry].Read();
    }
}
