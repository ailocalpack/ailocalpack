using System.Globalization;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Updates;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings.About;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop;

/// <summary>
/// <c>--smoke-test</c> (task 71): the release workflow runs the packaged app this way on every OS and expects exit 0.
/// No display needed: Avalonia's headless platform with real Skia rendering. In a throwaway app home (never the
/// player's folders, no Steam, no registry, no update feed), it checks what packaging can break: every UI language's
/// satellite resources, the embedded third-party notices, the service graph, the XAML and the native Skia / HarfBuzz
/// libraries (the main window built and one frame rendered). Prints one line per check and the app's version.
/// </summary>
internal static class SmokeTest
{
    public const string Argument = "--smoke-test";

    public static bool IsRequested(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    public static int Run(TextWriter output)
    {
        var home = Directory.CreateTempSubdirectory("ailocalpack-smoke-");
        var step = "start";
        try
        {
            step = "UI languages";
            foreach (var tag in UiLanguages.Supported)
            {
                var culture = tag == UiLanguages.Fallback ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(tag);
                _ = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                    ?? throw new InvalidOperationException($"no resources for {tag}");
            }

            output.WriteLine($"ok: UI languages ({string.Join(", ", UiLanguages.Supported)})");

            step = "third-party notices";
            _ = ThirdPartyNoticesText.Load() ?? throw new InvalidOperationException("the embedded notices are missing");
            output.WriteLine("ok: third-party notices");

            step = "Avalonia";
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();
            output.WriteLine("ok: Avalonia (headless, Skia)");

            step = "services";
            var env = new OsEnvironment(
                OsEnvironment.Current.Os,
                Directory.CreateDirectory(Path.Combine(home.FullName, "user")).FullName,
                name => name switch
                {
                    SteamLocator.RootVariable => Path.Combine(home.FullName, "no-steam"),
                    RegistryLocation.IndexUrlVariable => Path.Combine(home.FullName, "no-registry"),
                    VelopackUpdateSource.UrlVariable => Path.Combine(home.FullName, "no-updates"),
                    _ => null,
                });
            using var services = Composition.BuildServices(NullLoggerFactory.Instance, AppPaths.UnderHome(home.FullName), env);
            var updater = services.GetRequiredService<AppUpdater>();
            output.WriteLine("ok: services");

            step = "main window";
            var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered"))
            {
                output.WriteLine($"ok: main window rendered ({frame.PixelSize.Width}×{frame.PixelSize.Height})");
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();

            var version = updater.CurrentVersion ?? typeof(SmokeTest).Assembly.GetName().Version?.ToString(3) ?? "?";
            output.WriteLine($"version: {version} ({updater.Install})");
            output.WriteLine("smoke test passed");
            return 0;
        }
        catch (Exception e)
        {
            output.WriteLine($"smoke test failed at {step}: {e}");
            return 1;
        }
        finally
        {
            output.Flush();
            try
            {
                home.Delete(recursive: true);
            }
            catch (IOException)
            {
                // a log file still open: the OS cleans its temp folder
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
