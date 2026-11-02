using AiLocalPack.Desktop.ViewModels;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = Composition.BuildServices(LoggerFactory);
            var logger = LoggerFactory.CreateLogger<App>();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
                logger.LogCritical(e.Exception, "Unhandled UI thread exception");
            desktop.MainWindow = new MainWindow { DataContext = _services.GetRequiredService<MainWindowViewModel>() };
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
