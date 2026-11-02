using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop;

/// <summary>The app's service graph. Tests build it too, with their own logger factory.</summary>
internal static class Composition
{
    /// <param name="configure">Runs last, so tests can replace services (a later registration wins).</param>
    public static ServiceProvider BuildServices(ILoggerFactory loggerFactory, AppPaths paths, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(Localizer.Current);
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IPageFactory, PageFactory>();
        services.AddSingleton<IAppTheming, AvaloniaAppTheming>();
        services.AddSingleton<SettingsApplier>();
        services.AddSingleton<MainWindowViewModel>();
        AddSettingsSections(services);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Settings sections, in the order of the section list. A section without rows yet shows "coming in a later version".</summary>
    private static void AddSettingsSections(IServiceCollection services)
    {
        services.AddSingleton<GeneralSettingsSection>();
        services.AddSingleton<SettingsSection>(sp => sp.GetRequiredService<GeneralSettingsSection>());
        AddPlaceholder(SettingsSectionId.Libraries, "IconFolder", nameof(Strings.Settings_Libraries_Title), nameof(Strings.Settings_Libraries_Description));
        AddPlaceholder(SettingsSectionId.Agents, "IconBot", nameof(Strings.Settings_Agents_Title), nameof(Strings.Settings_Agents_Description));
        AddPlaceholder(SettingsSectionId.GitHub, "IconGitHub", nameof(Strings.Settings_GitHub_Title), nameof(Strings.Settings_GitHub_Description));
        AddPlaceholder(SettingsSectionId.Storage, "IconHardDrive", nameof(Strings.Settings_Storage_Title), nameof(Strings.Settings_Storage_Description));
        AddPlaceholder(SettingsSectionId.About, "IconInfo", nameof(Strings.Settings_About_Title), nameof(Strings.Settings_About_Description));

        void AddPlaceholder(SettingsSectionId id, string icon, string title, string description) =>
            services.AddSingleton(sp => new SettingsSection(id, icon, title, description, sp.GetRequiredService<Localizer>()));
    }
}
