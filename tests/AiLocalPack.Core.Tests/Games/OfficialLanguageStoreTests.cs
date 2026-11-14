using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Tests.Games;

public sealed class OfficialLanguageStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameId Brotato = GameId.Steam(1942280);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string File => _dir.Combine("official-languages.json");

    private static OfficialLanguages Russian(OfficialLanguagesSource source = OfficialLanguagesSource.SteamClient, DateTimeOffset? at = null) =>
        new([new OfficialLanguage("en", true, true, true), new OfficialLanguage("ru", true, false, false)], source, at ?? Now, "en");

    [Fact]
    public void Remembers_languages_across_instances()
    {
        // Arrange
        new OfficialLanguageStore(File).Set([new(Brotato, Russian())]);

        // Act
        var read = new OfficialLanguageStore(File).Find(Brotato);

        // Assert
        read.Should().BeEquivalentTo(Russian());
    }

    [Fact]
    public void Reports_only_games_whose_languages_changed()
    {
        // Arrange
        var store = new OfficialLanguageStore(File);
        store.Set([new(Brotato, Russian())]);

        // Act
        var changed = store.Set([new(Brotato, Russian(at: Now.AddDays(1)))]);

        // Assert
        changed.Should().BeEmpty();
    }

    [Fact]
    public void The_same_store_answer_again_renews_its_check_time()
    {
        // Arrange
        var store = new OfficialLanguageStore(File);
        store.Set([new(Brotato, Russian(OfficialLanguagesSource.SteamStore))]);

        // Act
        store.Set([new(Brotato, Russian(OfficialLanguagesSource.SteamStore, Now.AddDays(8)))]);

        // Assert
        new OfficialLanguageStore(File).Find(Brotato)!.Checked.Should().Be(Now.AddDays(8));
    }

    [Fact]
    public void Forgets_games_no_longer_in_the_library()
    {
        // Arrange
        var store = new OfficialLanguageStore(File);
        store.Set([new(Brotato, Russian()), new(GameId.Steam(10), Russian())]);

        // Act
        store.Keep([GameId.Steam(10)]);

        // Assert
        new OfficialLanguageStore(File).Find(Brotato).Should().BeNull();
    }

    [Fact]
    public void A_broken_file_starts_empty()
    {
        // Arrange
        System.IO.File.WriteAllText(File, "{ not json");

        // Act
        var read = new OfficialLanguageStore(File).Find(Brotato);

        // Assert
        read.Should().BeNull();
    }

    [Fact]
    public void A_newer_format_file_is_not_overwritten()
    {
        // Arrange
        var newer = """{"schemaVersion":2,"games":[]}""";
        System.IO.File.WriteAllText(File, newer);
        var store = new OfficialLanguageStore(File);

        // Act
        store.Set([new(Brotato, Russian())]);

        // Assert
        System.IO.File.ReadAllText(File).Should().Be(newer);
    }

    [Fact]
    public void Skips_entries_with_unknown_language_tags()
    {
        // Arrange
        System.IO.File.WriteAllText(File, """
            {"schemaVersion":1,"games":[{"game":"steam:1942280","source":"steam-client","checked":"2026-11-14T12:00:00+00:00",
              "languages":[{"tag":"tlh","interface":true},{"tag":"RU","interface":true}]}]}
            """);

        // Act
        var read = new OfficialLanguageStore(File).Find(Brotato);

        // Assert
        read!.Languages.Should().Equal(new OfficialLanguage("ru", true, false, false));
    }
}
