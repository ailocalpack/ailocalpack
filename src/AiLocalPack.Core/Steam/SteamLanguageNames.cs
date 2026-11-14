using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Steam;

/// <summary>
/// Steam's names of the languages it supports and the <see cref="GameLanguages.Tags"/> tags they stand for: the API
/// names of the client's app info and app manifests (<c>russian</c>, <c>schinese</c>, <c>latam</c>) and the English names
/// of the store's <c>supported_languages</c> (<c>Simplified Chinese</c>, <c>Spanish - Latin America</c>).
/// </summary>
public static class SteamLanguageNames
{
    private static readonly Dictionary<string, string> ApiToTag = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arabic"] = "ar",
        ["bulgarian"] = "bg",
        ["czech"] = "cs",
        ["danish"] = "da",
        ["german"] = "de",
        ["greek"] = "el",
        ["english"] = "en",
        ["spanish"] = "es-ES",
        ["latam"] = "es-419",
        ["finnish"] = "fi",
        ["french"] = "fr",
        ["hungarian"] = "hu",
        ["indonesian"] = "id",
        ["italian"] = "it",
        ["japanese"] = "ja",
        ["koreana"] = "ko",
        ["norwegian"] = "nb",
        ["dutch"] = "nl",
        ["polish"] = "pl",
        ["brazilian"] = "pt-BR",
        ["portuguese"] = "pt-PT",
        ["romanian"] = "ro",
        ["russian"] = "ru",
        ["swedish"] = "sv",
        ["thai"] = "th",
        ["turkish"] = "tr",
        ["ukrainian"] = "uk",
        ["vietnamese"] = "vi",
        ["schinese"] = "zh-Hans",
        ["tchinese"] = "zh-Hant",
    };

    private static readonly Dictionary<string, string> EnglishToTag = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arabic"] = "ar",
        ["Bulgarian"] = "bg",
        ["Czech"] = "cs",
        ["Danish"] = "da",
        ["German"] = "de",
        ["Greek"] = "el",
        ["English"] = "en",
        ["Spanish - Spain"] = "es-ES",
        ["Spanish"] = "es-ES",
        ["Spanish - Latin America"] = "es-419",
        ["Finnish"] = "fi",
        ["French"] = "fr",
        ["Hungarian"] = "hu",
        ["Indonesian"] = "id",
        ["Italian"] = "it",
        ["Japanese"] = "ja",
        ["Korean"] = "ko",
        ["Norwegian"] = "nb",
        ["Dutch"] = "nl",
        ["Polish"] = "pl",
        ["Portuguese - Brazil"] = "pt-BR",
        ["Portuguese - Portugal"] = "pt-PT",
        ["Portuguese"] = "pt-PT",
        ["Romanian"] = "ro",
        ["Russian"] = "ru",
        ["Swedish"] = "sv",
        ["Thai"] = "th",
        ["Turkish"] = "tr",
        ["Ukrainian"] = "uk",
        ["Vietnamese"] = "vi",
        ["Simplified Chinese"] = "zh-Hans",
        ["Traditional Chinese"] = "zh-Hant",
    };

    /// <summary>The tag of a Steam API language name (<c>koreana</c> → <c>ko</c>); <c>null</c> for a name Steam doesn't sell games in.</summary>
    public static string? FromApiName(string? name) => name is not null && ApiToTag.TryGetValue(name.Trim(), out var tag) ? tag : null;

    /// <summary>The tag of a store language name (<c>Portuguese - Brazil</c> → <c>pt-BR</c>); <c>null</c> when unknown.</summary>
    public static string? FromEnglishName(string? name) => name is not null && EnglishToTag.TryGetValue(name.Trim(), out var tag) ? tag : null;
}
