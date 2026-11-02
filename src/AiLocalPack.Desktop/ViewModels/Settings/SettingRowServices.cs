using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Localization;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>What every settings row needs: the store it edits, UI strings, a log for failed saves.</summary>
public sealed record SettingRowServices(SettingsStore Store, Localizer Localizer, ILogger Logger);
