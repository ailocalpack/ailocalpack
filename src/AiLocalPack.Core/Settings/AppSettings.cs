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
/// <param name="SteamLibraryFolders">
/// Steam library folders the player added by hand (Settings → Libraries): libraries on drives Steam doesn't list.
/// <c>null</c> = none (read through <see cref="SteamLibraries"/>).
/// </param>
/// <param name="IgnoredSteamLibraries">
/// Library folders Steam lists that the player removed (a gone SD card); the scan leaves them out until added again.
/// </param>
/// <param name="DefaultAgent">
/// The agent preselected in the generation form (Settings → AI agents), an agent ID (<c>claude</c>, <c>codex</c>);
/// <c>null</c> or an agent that isn't ready = the first ready one.
/// </param>
/// <param name="SignedInAgents">
/// Agents the app has seen signed in and the player didn't sign out of here: one found signed out later is shown as
/// <em>Session expired</em> (a warning), not as never signed in. <c>null</c> = none.
/// </param>
/// <param name="RegistryIndexUrl">
/// Hidden dev setting (no UI): another registry index, an <c>https://</c> URL or a local folder
/// (<see cref="Registry.RegistryLocation.TryParse"/>); <c>AILOCALPACK_INDEX_URL</c> wins over it. <c>null</c> = the public registry.
/// </param>
/// <param name="PublishLicenseAccepted">
/// The player ticked "I publish under CC BY-SA 4.0" in the first publish dialog (task 61): later publishes don't ask again.
/// </param>
/// <param name="WelcomeTourShown">
/// The welcome tour was shown and closed (finished, skipped or Esc): it no longer opens by itself on start (design §10);
/// <c>?</c> in the chrome and Settings → About still show it.
/// </param>
/// <param name="TranslateWorkers">
/// How many chunks the Translate stage gives the agent at once (Settings → AI agents): each is its own agent run with
/// its own toolkit server, so more is faster but loads the PC and spends the agent's limit sooner. Read through
/// <see cref="TranslateWorkerCount"/> (clamped to <see cref="MinTranslateWorkers"/>–<see cref="MaxTranslateWorkers"/>);
/// a job takes the value when it starts or resumes.
/// </param>
public sealed record AppSettings(
    int SchemaVersion = AppSettings.CurrentSchemaVersion,
    string? UiLanguage = null,
    string? GameLanguage = null,
    AppTheme Theme = AppTheme.System,
    bool RunInBackground = true,
    bool ReapplyAfterGameUpdates = true,
    bool NotifyNeedsDecision = true,
    bool NotifyTaskFinished = true,
    bool NotifyCommunity = true,
    IReadOnlyList<string>? SteamLibraryFolders = null,
    IReadOnlyList<string>? IgnoredSteamLibraries = null,
    string? RegistryIndexUrl = null,
    string? DefaultAgent = null,
    IReadOnlyList<string>? SignedInAgents = null,
    bool PublishLicenseAccepted = false,
    bool WelcomeTourShown = false,
    int TranslateWorkers = AppSettings.DefaultTranslateWorkers)
{
    public const int CurrentSchemaVersion = 1;

    public const int DefaultTranslateWorkers = 2;

    public const int MinTranslateWorkers = 1;

    public const int MaxTranslateWorkers = 8;

    /// <summary><see cref="TranslateWorkers"/> within <see cref="MinTranslateWorkers"/>–<see cref="MaxTranslateWorkers"/> (a hand-edited file may hold anything).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TranslateWorkerCount => Math.Clamp(TranslateWorkers, MinTranslateWorkers, MaxTranslateWorkers);

    public static AppSettings Defaults { get; } = new();

    /// <summary>The player's changes to the Steam library list, for <see cref="Steam.SteamLibraryScanner.Scan(Steam.SteamLibraryChoices)"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Steam.SteamLibraryChoices SteamLibraries => new(SteamLibraryFolders ?? [], IgnoredSteamLibraries ?? []);
}
