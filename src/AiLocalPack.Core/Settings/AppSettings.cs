namespace AiLocalPack.Core.Settings;

/// <summary>
/// The player's settings, stored as <c>settings.json</c> in <see cref="Storage.AppPaths.Config"/>. Immutable: change with
/// <c>with</c> through <see cref="SettingsStore.Update"/>. A new property with a default needs no migration; renaming
/// or reshaping one bumps <see cref="CurrentSchemaVersion"/> and adds a step to <see cref="SettingsMigrator.Default"/>.
/// </summary>
/// <remarks>
/// Defaults are constructor parameter defaults, not property initializers: the System.Text.Json source generator
/// sets an init-only property missing from the file to <c>default</c> (a missing <c>true</c> flag would read as
/// <c>false</c>), while a missing constructor parameter gets its declared default. Add new settings as parameters.
/// </remarks>
/// <param name="SchemaVersion">Version of the file layout; see <see cref="SettingsMigrator"/>.</param>
/// <param name="UiLanguage">Interface language tag (<c>ru</c>, <c>en-GB</c>); <c>null</c> follows the OS.</param>
/// <param name="GameLanguage">
/// Default language games are translated into (browsing packs, generation), a tag from
/// <see cref="Languages.GameLanguages.Tags"/>; <c>null</c> follows the OS. Each game page can pick another.
/// </param>
/// <param name="Theme">Colour theme.</param>
/// <param name="RunInBackground">Start with the system and keep running in the tray (applied from task 39).</param>
/// <param name="ReapplyAfterGameUpdates">Re-apply a pack when a game update wiped it and the build is still compatible (task 33).</param>
/// <param name="NotifyNeedsDecision">System notification when something needs the player's decision (task 39).</param>
/// <param name="NotifyTaskFinished">System notification when a task finishes (task 39).</param>
/// <param name="NotifyCommunity">System notification about community activity on the player's packs (task 39).</param>
public sealed record AppSettings(
    int SchemaVersion = AppSettings.CurrentSchemaVersion,
    string? UiLanguage = null,
    string? GameLanguage = null,
    AppTheme Theme = AppTheme.System,
    bool RunInBackground = true,
    bool ReapplyAfterGameUpdates = true,
    bool NotifyNeedsDecision = true,
    bool NotifyTaskFinished = true,
    bool NotifyCommunity = true)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings Defaults { get; } = new();
}
