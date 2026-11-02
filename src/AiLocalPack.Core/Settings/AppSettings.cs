namespace AiLocalPack.Core.Settings;

/// <summary>
/// The player's settings, stored as <c>settings.json</c> in <see cref="Storage.AppPaths.Config"/>. Immutable: change with
/// <c>with</c> through <see cref="SettingsStore.Update"/>. A new property with a default needs no migration; renaming
/// or reshaping one bumps <see cref="CurrentSchemaVersion"/> and adds a step to <see cref="SettingsMigrator.Default"/>.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings Defaults { get; } = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Interface language tag (<c>ru</c>, <c>en-GB</c>); <c>null</c> follows the OS.</summary>
    public string? UiLanguage { get; init; }
}
