using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Tests.Steam;

public sealed class SteamLanguageNamesTests
{
    private static readonly string[] ApiNames =
    [
        "arabic", "bulgarian", "czech", "danish", "german", "greek", "english", "spanish", "latam", "finnish", "french",
        "hungarian", "indonesian", "italian", "japanese", "koreana", "norwegian", "dutch", "polish", "brazilian",
        "portuguese", "romanian", "russian", "swedish", "thai", "turkish", "ukrainian", "vietnamese", "schinese", "tchinese",
    ];

    [Fact]
    public void Every_language_steam_sells_games_in_has_an_api_name()
    {
        // Act
        var tags = ApiNames.Select(SteamLanguageNames.FromApiName);

        // Assert
        tags.Should().BeEquivalentTo(GameLanguages.Tags);
    }

    [Theory]
    [InlineData("Portuguese - Brazil", "pt-BR")]
    [InlineData("Traditional Chinese", "zh-Hant")]
    [InlineData(" Korean ", "ko")]
    public void Maps_store_names(string name, string tag)
    {
        // Act
        var mapped = SteamLanguageNames.FromEnglishName(name);

        // Assert
        mapped.Should().Be(tag);
    }

    [Fact]
    public void An_unknown_api_name_has_no_tag()
    {
        // Act
        var mapped = SteamLanguageNames.FromApiName("sc_schinese");

        // Assert
        mapped.Should().BeNull();
    }
}
