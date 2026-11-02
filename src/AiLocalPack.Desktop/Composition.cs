using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop;

/// <summary>The app's service graph. Tests build it too, with their own logger factory.</summary>
internal static class Composition
{
    public static ServiceProvider BuildServices(ILoggerFactory loggerFactory, AppPaths paths)
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
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
