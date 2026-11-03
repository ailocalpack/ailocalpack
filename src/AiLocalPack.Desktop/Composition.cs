using System.Net.Http.Headers;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.AddGame;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop;

/// <summary>The app's service graph. Tests build it too, with their own logger factory.</summary>
internal static class Composition
{
    /// <param name="configure">Runs last, so tests can replace services (a later registration wins).</param>
    public static ServiceProvider BuildServices(
        ILoggerFactory loggerFactory, AppPaths paths, OsEnvironment env, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(env);
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(Localizer.Current);
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IPageFactory, PageFactory>();
        services.AddSingleton<IAppTheming, AvaloniaAppTheming>();
        services.AddSingleton<SettingsApplier>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<ISystemShell, SystemShell>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFilePicker, AvaloniaFilePicker>();
        AddGames(services);
        services.AddSingleton<MainWindowViewModel>();
        AddSettingsSections(services);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Game catalog, Steam scan, detection and covers.</summary>
    private static void AddGames(IServiceCollection services)
    {
        services.AddSingleton<GameCatalog>();
        services.AddSingleton(sp => new SteamLocator(sp.GetRequiredService<OsEnvironment>()));
        services.AddSingleton(sp => new SteamLibraryScanner(sp.GetRequiredService<SteamLocator>(), sp.GetService<ILogger<SteamLibraryScanner>>()));
        services.AddSingleton(sp => new EngineDetector(sp.GetService<ILogger<EngineDetector>>()));
        services.AddSingleton(sp => AntiCheatPolicy.Bundled);
        services.AddSingleton(sp => new AntiCheatDetector(sp.GetRequiredService<AntiCheatPolicy>(), sp.GetService<ILogger<AntiCheatDetector>>()));
        services.AddSingleton<GameLibrary>();
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ailocalpack", typeof(Composition).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            return http;
        });
        services.AddSingleton(sp => new CoverResolver(
            sp.GetRequiredService<SteamLocator>().FindRoots,
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CoverResolver>>()));
        services.AddSingleton<ManualCoverStore>();
        services.AddSingleton<ICoverLoader, CoverLoader>();

        // Adding a game folder (design §2)
        services.AddSingleton(sp => FileHashCache.Load(sp.GetRequiredService<AppPaths>().FileHashes, sp.GetService<ILogger<FileHashCache>>()));
        services.AddSingleton(sp => new FolderRecognizer(sp.GetRequiredService<EngineDetector>(), sp.GetService<ILogger<FolderRecognizer>>()));
        services.AddSingleton(sp => new GameFingerprinter(sp.GetRequiredService<FileHashCache>(), sp.GetService<ILogger<GameFingerprinter>>()));
        services.AddSingleton(sp => new GameIconExtractor(sp.GetService<ILogger<GameIconExtractor>>()));
        services.AddSingleton<GameFolderAnalyzer>();
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
