using System.Globalization;
using AiLocalPack.Core.Languages;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>
/// Options of the language pickers: "System (&lt;OS language&gt;)" first (value <c>null</c>, the default, so the OS
/// language is preselected), then every language by its native name, sorted.
/// </summary>
public static class LanguageOptions
{
    /// <summary>Interface languages: the ones the app UI ships in.</summary>
    public static IReadOnlyList<ChoiceOption> ForUi(Localizer localizer, CultureInfo os)
    {
        ArgumentNullException.ThrowIfNull(os);
        var system = LanguageNames.Native(UiLanguages.Resolve(null, os).TwoLetterISOLanguageName);
        return Build(localizer, system, UiLanguages.Supported);
    }

    /// <summary>Languages games can be translated into.</summary>
    public static IReadOnlyList<ChoiceOption> ForGames(Localizer localizer, CultureInfo os)
    {
        ArgumentNullException.ThrowIfNull(os);
        var system = LanguageNames.Native(GameLanguages.Resolve(null, os));
        return Build(localizer, system, GameLanguages.Tags);
    }

    private static List<ChoiceOption> Build(Localizer localizer, string systemName, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var languages = tags
            .Select(tag => (Tag: tag, Name: LanguageNames.Native(tag)))
            .OrderBy(l => l.Name, LanguageNames.Order)
            .Select(l => new ChoiceOption(l.Tag, () => l.Name));
        return [new ChoiceOption(null, () => localizer.Format(nameof(Strings.Settings_Language_System), systemName)), .. languages];
    }
}
