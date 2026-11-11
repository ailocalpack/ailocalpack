using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>Settings → General: languages, theme, background, notifications (design §8).</summary>
public sealed class GeneralSettingsSection : SettingsSection
{
    /// <summary>Where players fix UI translations.</summary>
    public static Uri TranslationsUri { get; } = new("https://github.com/ailocalpack/ailocalpack/blob/master/CONTRIBUTING.md#app-ui-translations");

    public GeneralSettingsSection(SettingsStore store, Localizer localizer, ILogger<GeneralSettingsSection> logger)
        : base(SettingsSectionId.General, "IconSliders", nameof(Strings.Settings_General_Title), nameof(Strings.Settings_General_Description), localizer)
    {
        var services = new SettingRowServices(store, localizer, logger);

        UiLanguage = new DropdownSettingRow(
            services,
            nameof(Strings.Settings_UiLanguage_Label),
            nameof(Strings.Settings_UiLanguage_Help),
            nameof(Strings.Settings_UiLanguage_Keywords),
            LanguageOptions.ForUi(localizer, UiLanguages.Os),
            s => s.UiLanguage,
            (s, v) => s with { UiLanguage = (string?)v },
            nameof(Strings.Settings_UiLanguage_FixLink),
            TranslationsUri);
        GameLanguage = new DropdownSettingRow(
            services,
            nameof(Strings.Settings_GameLanguage_Label),
            nameof(Strings.Settings_GameLanguage_Help),
            nameof(Strings.Settings_GameLanguage_Keywords),
            LanguageOptions.ForGames(localizer, UiLanguages.Os),
            s => s.GameLanguage,
            (s, v) => s with { GameLanguage = (string?)v });
        Theme = new SegmentedSettingRow(
            services,
            nameof(Strings.Settings_Theme_Label),
            nameof(Strings.Settings_Theme_Help),
            nameof(Strings.Settings_Theme_Keywords),
            [
                new ChoiceOption(AppTheme.System, () => localizer[nameof(Strings.Settings_Theme_System)]),
                new ChoiceOption(AppTheme.Dark, () => localizer[nameof(Strings.Settings_Theme_Dark)]),
                new ChoiceOption(AppTheme.Light, () => localizer[nameof(Strings.Settings_Theme_Light)]),
            ],
            s => s.Theme,
            (s, v) => s with { Theme = (AppTheme)v! });
        RunInBackground = new ToggleSettingRow(
            services,
            nameof(Strings.Settings_RunInBackground_Label),
            _ => nameof(Strings.Settings_RunInBackground_Help),
            nameof(Strings.Settings_RunInBackground_Keywords),
            s => s.RunInBackground,
            (s, v) => s with { RunInBackground = v });
        ReapplyAfterGameUpdates = new ToggleSettingRow(
            services,
            nameof(Strings.Settings_Reapply_Label),
            s => !s.ReapplyAfterGameUpdates ? nameof(Strings.Settings_Reapply_HelpOff)
                : s.RunInBackground ? nameof(Strings.Settings_Reapply_Help)
                : nameof(Strings.Settings_Reapply_HelpTrayOff),
            nameof(Strings.Settings_Reapply_Keywords),
            s => s.ReapplyAfterGameUpdates,
            (s, v) => s with { ReapplyAfterGameUpdates = v });
        NotifyNeedsDecision = Notification(services, nameof(Strings.Settings_NotifyDecision_Label), nameof(Strings.Settings_NotifyDecision_Help),
            s => s.NotifyNeedsDecision, (s, v) => s with { NotifyNeedsDecision = v });
        NotifyTaskFinished = Notification(services, nameof(Strings.Settings_NotifyFinished_Label), nameof(Strings.Settings_NotifyFinished_Help),
            s => s.NotifyTaskFinished, (s, v) => s with { NotifyTaskFinished = v });
        NotifyCommunity = Notification(services, nameof(Strings.Settings_NotifyCommunity_Label), nameof(Strings.Settings_NotifyCommunity_Help),
            s => s.NotifyCommunity, (s, v) => s with { NotifyCommunity = v });

        Groups =
        [
            new SettingsGroup(services, nameof(Strings.Settings_Group_Language), [UiLanguage, GameLanguage]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Appearance), [Theme]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Background), [RunInBackground, ReapplyAfterGameUpdates]),
            new SettingsGroup(
                services,
                nameof(Strings.Settings_Group_Notifications),
                [NotifyNeedsDecision, NotifyTaskFinished, NotifyCommunity],
                s => s.RunInBackground ? nameof(Strings.Settings_Notifications_Note) : nameof(Strings.Settings_Notifications_NoteTrayOff)),
        ];
    }

    public DropdownSettingRow UiLanguage { get; }

    public DropdownSettingRow GameLanguage { get; }

    public SegmentedSettingRow Theme { get; }

    public ToggleSettingRow RunInBackground { get; }

    public ToggleSettingRow ReapplyAfterGameUpdates { get; }

    public ToggleSettingRow NotifyNeedsDecision { get; }

    public ToggleSettingRow NotifyTaskFinished { get; }

    public ToggleSettingRow NotifyCommunity { get; }

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    private static ToggleSettingRow Notification(
        SettingRowServices services, string labelKey, string helpKey, Func<AppSettings, bool> read, Func<AppSettings, bool, AppSettings> write) =>
        new(services, labelKey, _ => helpKey, nameof(Strings.Settings_Notify_Keywords), read, write);
}
