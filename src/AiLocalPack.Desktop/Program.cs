using AiLocalPack.Desktop.Logging;
using Avalonia;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var loggerFactory = AppLogging.CreateFactory(AppLogging.DefaultDirectory);
        var logger = loggerFactory.CreateLogger("AiLocalPack");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        logger.LogInformation("Starting ailocalpack on {Os}", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        App.LoggerFactory = loggerFactory;
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
