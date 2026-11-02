using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Logging;
using Avalonia;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
        App.LoggerFactory = loggerFactory;
        App.Paths = paths;
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
