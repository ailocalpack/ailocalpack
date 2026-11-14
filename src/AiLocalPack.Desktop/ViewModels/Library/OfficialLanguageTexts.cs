using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.Library;

/// <summary>How much of the game is in its own language (task 80), in the player's words.</summary>
public static class OfficialLanguageTexts
{
    /// <summary>
    /// "interface, subtitles, full audio" from the Steam client's split; "text, full audio" from the store, which doesn't
    /// tell interface from subtitles.
    /// </summary>
    public static string Levels(OfficialLanguage language, OfficialLanguagesSource source, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(localizer);
        var parts = new List<string>();
        if (source == OfficialLanguagesSource.SteamStore)
        {
            parts.Add(localizer[nameof(Strings.Official_Level_Text)]);
        }
        else
        {
            if (language.Interface)
            {
                parts.Add(localizer[nameof(Strings.Official_Level_Interface)]);
            }

            if (language.Subtitles)
            {
                parts.Add(localizer[nameof(Strings.Official_Level_Subtitles)]);
            }
        }

        if (language.FullAudio)
        {
            parts.Add(localizer[nameof(Strings.Official_Level_Audio)]);
        }

        return string.Join(localizer[nameof(Strings.Official_Level_Separator)], parts);
    }
}
