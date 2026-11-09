using System.Net;
using System.Net.Http.Headers;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Core.Background;
using AiLocalPack.Desktop.AddGame;
using AiLocalPack.Desktop.Background;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
using AiLocalPack.Desktop.ViewModels.Settings.Libraries;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Dependencies;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using AiLocalPack.Agents.Install;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Desktop.Agents;

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
        services.AddSingleton(sp => new RegistryIndexClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RegistryIndexClient>>()));
        services.AddSingleton<PackIndex>();
        services.AddSingleton(sp => new PackManifestClient(sp.GetRequiredService<HttpClient>(), logger: sp.GetRequiredService<ILogger<PackManifestClient>>()));
        services.AddSingleton(RecipeTools.Create(env));
        services.AddSingleton<IRecipeTools>(sp => sp.GetRequiredService<RecipeTools>());
        services.AddSingleton(sp => new PackCompatibilityChecker(sp.GetRequiredService<FileHashCache>(), sp.GetRequiredService<IRecipeTools>()));
        services.AddSingleton<PackManifests>();
        services.AddSingleton(sp => new PackCache(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<PackCache>>()));
        services.AddSingleton(sp => new PackFetcher(
            sp.GetRequiredService<PackManifestClient>(),
            PackFetcher.CreateHttpClient(new ProductInfoHeaderValue("ailocalpack", typeof(Composition).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")),
            sp.GetRequiredService<PackCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PackFetcher>>()));
        services.AddSingleton(FontWhitelist.Bundled);
        services.AddSingleton(sp => new FontStore(
            sp.GetRequiredService<AppPaths>(),
            PackFetcher.CreateHttpClient(new ProductInfoHeaderValue("ailocalpack", typeof(Composition).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<FontStore>>()));
        services.AddSingleton(sp => new FontFixer(sp.GetRequiredService<FontWhitelist>(), sp.GetRequiredService<FontStore>(), sp.GetRequiredService<ILogger<FontFixer>>()));
        services.AddSingleton(sp => new DependencyStore(
            sp.GetRequiredService<AppPaths>(),
            PackFetcher.CreateHttpClient(new ProductInfoHeaderValue("ailocalpack", typeof(Composition).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DependencyStore>>()));
        services.AddSingleton(sp => new DependencyPreparer(sp.GetRequiredService<DependencyStore>(), sp.GetRequiredService<EngineDetector>()));
        services.AddSingleton(sp => new InstalledPackStore(
            sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<InstalledPackStore>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PackInstaller(
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<InstalledPackStore>(),
            sp.GetRequiredService<AntiCheatPolicy>(),
            sp.GetRequiredService<IProcessList>(),
            sp.GetRequiredService<OsEnvironment>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PackInstaller>>()));
        services.AddSingleton<IInstalledPacks, StoreInstalledPacks>();
        services.AddSingleton<PackInstallService>();
        services.AddSingleton<InstalledPackMonitor>();
        services.AddSingleton<ToastService>();
        services.AddSingleton(sp => new ActivityStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ActivityStore>>()));
        services.AddSingleton<ActivityRecorder>();
        services.AddSingleton<GenerationActivityRecorder>();
        services.AddSingleton<InstallFlow>();
        services.AddSingleton<ActivityPanelViewModel>();
        services.AddSingleton<GameStatuses>();
        AddBackground(services, env, paths, loggerFactory);
        AddAgents(services);
        AddGeneration(services);
        services.AddSingleton<AppStartup>();
        services.AddSingleton(sp => sp.GetRequiredService<AppStartup>().Progress);
        services.AddTransient<SplashViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        AddSettingsSections(services);
        services.AddSingleton<SettingsPreloader>();
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Tray, start with the system, background re-checks, OS notifications (task 39).</summary>
    private static void AddBackground(IServiceCollection services, OsEnvironment env, AppPaths paths, ILoggerFactory loggerFactory)
    {
        services.AddSingleton<IWindowsRegistryWriter>(WindowsRegistry.Instance);
        services.AddSingleton(sp => Autostart.For(env, sp.GetRequiredService<IWindowsRegistryWriter>()));
        // A throwaway or portable home (AILOCALPACK_HOME: dev, tests) never registers itself at sign-in.
        services.AddSingleton(_ => new AutostartTarget(env.GetVariable(AppPaths.HomeVariable) is null
            ? AutostartCommand.ForCurrentProcess(env, Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location)
            : null));
        services.AddSingleton(sp => new SteamManifestWatcher(sp.GetRequiredService<ILogger<SteamManifestWatcher>>()));
        services.AddSingleton<BackgroundChecks>();
        services.AddSingleton<AppWindowHost>();
        services.AddSingleton<IAppWindow>(sp => sp.GetRequiredService<AppWindowHost>());
        services.AddSingleton(_ => SystemNotifications.Create(env, paths, loggerFactory));
        services.AddSingleton<ActivityNotifier>();
        services.AddSingleton<TrayHost>();
    }

    /// <summary>AI agents: their programs, the app's own copies, sign-in (tasks 40–44).</summary>
    private static void AddAgents(IServiceCollection services)
    {
        services.AddSingleton<IProcessRunner>(_ => new ProcessRunner());
        services.AddSingleton(sp =>
        {
            // Downloads keep the stored bytes (hashes are of them) and have their own timeouts.
            var http = AgentInstaller.CreateHttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ailocalpack", typeof(Composition).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            return AgentInstaller.Create(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<OsEnvironment>(), http, sp.GetRequiredService<ILogger<AgentInstaller>>());
        });
        services.AddSingleton<IAgentHost, AgentHost>();
    }

    /// <summary>Generation jobs: estimate, working copies, the pipeline's stages (tasks 46–52).</summary>
    private static void AddGeneration(IServiceCollection services)
    {
        services.AddSingleton<GenerationJobStore>();
        services.AddSingleton(sp => new WorkingCopies(sp.GetRequiredService<AppPaths>(), FileCloner.Create(), new SystemDiskSpace()));
        services.AddSingleton(sp => new GenerationEstimator(sp.GetRequiredService<RecipeTools>(), sp.GetRequiredService<WorkingCopies>(), sp.GetRequiredService<EngineDetector>()));
        services.AddSingleton<IGenerationEstimates, GenerationEstimates>();
        services.AddSingleton(sp => new LocalPackStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<LocalPackStore>>()));
        services.AddSingleton<IGenerationStagesFactory, PipelineStagesFactory>();
        services.AddSingleton<LocalPacks>();
        services.AddSingleton<GenerationService>();
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
        services.AddSingleton<IProcessList, SystemProcessList>();
        services.AddSingleton<IProcessStarter, SystemProcessStarter>();
        services.AddSingleton<GameSessions>();
        services.AddSingleton(_ =>
        {
            // GitHub Pages serves index.json compressed when asked.
            var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
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
        services.AddSingleton<LibrariesSettingsSection>();
        services.AddSingleton<SettingsSection>(sp => sp.GetRequiredService<LibrariesSettingsSection>());
        services.AddSingleton<AgentsSettingsSection>();
        services.AddSingleton<SettingsSection>(sp => sp.GetRequiredService<AgentsSettingsSection>());
        AddPlaceholder(SettingsSectionId.GitHub, "IconGitHub", nameof(Strings.Settings_GitHub_Title), nameof(Strings.Settings_GitHub_Description));
        AddPlaceholder(SettingsSectionId.Storage, "IconHardDrive", nameof(Strings.Settings_Storage_Title), nameof(Strings.Settings_Storage_Description));
        AddPlaceholder(SettingsSectionId.About, "IconInfo", nameof(Strings.Settings_About_Title), nameof(Strings.Settings_About_Description));

        void AddPlaceholder(SettingsSectionId id, string icon, string title, string description) =>
            services.AddSingleton(sp => new SettingsSection(id, icon, title, description, sp.GetRequiredService<Localizer>()));
    }
}
