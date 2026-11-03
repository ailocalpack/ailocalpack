using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop;

public sealed class App : Application
{
    private ServiceProvider? _services;

    /// <summary>Set by <see cref="Program"/> before start; the designer and tests run without file logging.</summary>
    internal static ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>Set by <see cref="Program"/> before start; otherwise resolved for this machine.</summary>
    internal static AppPaths? Paths { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = Composition.BuildServices(LoggerFactory, Paths ?? AppPaths.Resolve(OsEnvironment.Current), OsEnvironment.Current);
            var logger = LoggerFactory.CreateLogger<App>();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
                logger.LogCritical(e.Exception, "Unhandled UI thread exception");
            _services.GetRequiredService<SettingsStore>().Load();
            _services.GetRequiredService<SettingsApplier>().Start();
            // The library shows the saved catalog at once; the Steam scan refreshes it in the background.
            _services.GetRequiredService<GameCatalog>().Load();
            _ = _services.GetRequiredService<GameLibrary>().RefreshAsync();
            desktop.MainWindow = new MainWindow { DataContext = _services.GetRequiredService<MainWindowViewModel>() };
            // Once the window is up, so the first frame isn't delayed: Settings then opens without a pause.
            var preloader = _services.GetRequiredService<SettingsPreloader>();
            desktop.MainWindow.Opened += (_, _) => _ = preloader.PreloadAsync();
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
