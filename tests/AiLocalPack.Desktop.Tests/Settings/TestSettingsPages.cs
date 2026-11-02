using System.Globalization;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;

namespace AiLocalPack.Desktop.Tests.Settings;

/// <summary>Settings pages without the service graph, for navigation tests.</summary>
internal static class TestSettingsPages
{
    public static SettingsViewModel Create()
    {
        var localizer = new Localizer(CultureInfo.GetCultureInfo("en"));
        var section = new SettingsSection(
            SettingsSectionId.About, "IconInfo", nameof(Strings.Settings_About_Title), nameof(Strings.Settings_About_Description), localizer);
        return new SettingsViewModel([section], localizer);
    }
}
