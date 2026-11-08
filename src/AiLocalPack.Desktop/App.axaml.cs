using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop;

public sealed class App : Application
{
    /// <summary>The splash appears only when the blocking steps take longer than this (a fast start shows none)…</summary>
    internal static readonly TimeSpan SplashDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>…and once shown stays at least this long, so it never flashes.</summary>
    internal static readonly TimeSpan SplashMinimum = TimeSpan.FromMilliseconds(700);

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
            // No window is the main one until the library opens: closing the splash quits, the app doesn't stop before.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _services.Dispose();
            _ = StartAsync(desktop, _services, logger);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Design §9: the blocking steps run off the UI thread behind a splash window (shown only if they take a moment),
    /// then the main window opens on the library and the background checks start.
    /// </summary>
    private static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, ServiceProvider services, ILogger logger)
    {
        var startup = services.GetRequiredService<AppStartup>();
        SplashWindow? splash = null;
        var mainOpened = false;
        try
        {
            var blocking = startup.RunBlockingAsync();
            if (await Task.WhenAny(blocking, Task.Delay(SplashDelay)) != blocking)
            {
                splash = new SplashWindow { DataContext = services.GetRequiredService<SplashViewModel>() };
                splash.Closed += (_, _) =>
                {
                    if (!mainOpened)
                    {
                        desktop.Shutdown();
                    }
                };
                var shownAt = DateTimeOffset.UtcNow;
                splash.Show();
                await blocking;
                var left = SplashMinimum - (DateTimeOffset.UtcNow - shownAt);
                if (left > TimeSpan.Zero)
                {
                    await Task.Delay(left);
                }
            }
            else
            {
                await blocking;
            }
        }
        catch (Exception e)
        {
            // AppStartup never throws; if it did, the library still opens with what was loaded.
            logger.LogError(e, "Startup failed");
        }

        if (splash is { IsVisible: false })
        {
            return; // the player closed the splash: the app is quitting
        }

        var main = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        desktop.MainWindow = main;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainOpened = true;
        // Once the window is up, so the first frame isn't delayed: Settings then opens without a pause.
        main.Opened += (_, _) => _ = services.GetRequiredService<SettingsPreloader>().PreloadAsync();
        main.Show();
        main.Activate();
        splash?.Close();
        // Before the background checks: what they find (re-applied or broken packs) goes to the Activity panel.
        services.GetRequiredService<AiLocalPack.Desktop.Activity.ActivityRecorder>();
        startup.StartBackground();
    }
}
