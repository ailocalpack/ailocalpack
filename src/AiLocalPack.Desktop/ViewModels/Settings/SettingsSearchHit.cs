namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>A section in the search results with its matching rows.</summary>
public sealed record SettingsSearchHit(SettingsSection Section, IReadOnlyList<SettingRow> Rows);
