using AiLocalPack.Core.Background;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Background;
using AiLocalPack.Desktop.Logging;
using AiLocalPack.Mcp;
using Avalonia;
using Microsoft.Extensions.Logging;
using Velopack;

namespace AiLocalPack.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Hidden mode an agent starts (task 45): the toolkit MCP server on standard input / output, no window, no
        // single-instance check (the app itself is usually running), no logging that could touch standard output.
        if (ToolkitServerLaunch.IsRequested(args))
        {
            return ToolkitServerLaunch.Run(args, OsEnvironment.Current, Console.Error);
        }

        // Velopack (task 67): install / update / uninstall hooks exit here before any UI, and an update downloaded
        // earlier is applied on start. After the MCP server check, so an agent's toolkit server never restarts into one.
        VelopackApp.Build().Run();

        // The release workflow's check of the packaged app (task 71): headless, in a throwaway home; after Velopack, whose
        // locator tells the version and channel the package was made with.
        if (SmokeTest.IsRequested(args))
        {
            return SmokeTest.Run(Console.Out);
        }

        var paths = AppPaths.Resolve(OsEnvironment.Current);
        using var loggerFactory = AppLogging.CreateFactory(paths.Logs);
        var logger = loggerFactory.CreateLogger("AiLocalPack");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        logger.LogInformation(
            "Starting ailocalpack on {Os}; config {Config}, data {Data}, cache {Cache}",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, paths.Config, paths.Data, paths.Cache);
        var background = args.Contains(AutostartCommand.BackgroundArgument, StringComparer.Ordinal);
        using var instance = SingleInstance.TryAcquire(paths.Data, show: !background, logger);
        if (instance is null)
        {
            logger.LogInformation("ailocalpack already runs for this data folder; {Action}", background ? "nothing to do" : "asked it to show its window");
            return 0;
        }

        App.LoggerFactory = loggerFactory;
        App.Paths = paths;
        App.StartInBackground = background;
        App.Instance = instance;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error");
            throw;
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
