using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Background;

/// <summary>Whether closing the main window hides it to the tray instead of quitting.</summary>
public static class CloseToTray
{
    /// <param name="byPlayer">The player closed the window (title bar ✕, Alt+F4, ⌘W), not the app or OS shutting down.</param>
    /// <param name="trayOn">Run in the background is on and a tray icon can be shown.</param>
    /// <param name="quitting"><c>Quit</c> from the tray menu.</param>
    public static bool ShouldHide(bool byPlayer, bool trayOn, bool quitting) => byPlayer && trayOn && !quitting;
}

/// <summary>
/// The tray icon (task 39): shown while <see cref="AppSettings.RunInBackground"/> is on and the desktop has a tray
/// (Linux: a StatusNotifierItem host, see <see cref="FreeDesktopNotifications.HasTrayAsync"/>). Its menu: Open, Check
/// for game updates, Quit; clicking it (Windows, Linux) opens the window. While it shows, closing the window hides it
/// and the app keeps running (<see cref="ShutdownMode.OnExplicitShutdown"/>); otherwise closing quits. On macOS the Dock
/// icon reopens a hidden window.
/// </summary>
public sealed class TrayHost
{
    private readonly SettingsStore _settings;
    private readonly AppWindowHost _window;
    private readonly BackgroundChecks _checks;
    private readonly Localizer _localizer;
    private readonly ILogger<TrayHost> _logger;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _icon;
    private NativeMenuItem? _open;
    private NativeMenuItem? _check;
    private NativeMenuItem? _quit;
    private bool _quitting;

    public TrayHost(SettingsStore settings, AppWindowHost window, BackgroundChecks checks, Localizer localizer, ILogger<TrayHost> logger)
    {
        _settings = settings;
        _window = window;
        _checks = checks;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>The desktop can show a tray icon (set before <see cref="Attach"/>; Linux asks D-Bus).</summary>
    public bool TrayAvailable { get; set; } = true;

    /// <summary>The tray icon shows and closing the window hides it.</summary>
    public bool IsOn => TrayAvailable && _settings.Current.RunInBackground;

    /// <summary>Creates the icon and takes over the window's close. Call on the UI thread once the main window exists.</summary>
    public void Attach(Application app, IClassicDesktopStyleApplicationLifetime desktop, Window main)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(main);
        _desktop = desktop;
        _window.Attach(main);
        try
        {
            _open = new NativeMenuItem();
            _open.Click += (_, _) => _window.Show();
            _check = new NativeMenuItem();
            _check.Click += (_, _) => _ = _checks.CheckNowAsync();
            _quit = new NativeMenuItem();
            _quit.Click += (_, _) => Quit();
            using var image = AssetLoader.Open(new Uri("avares://AiLocalPack/Assets/tray.png"));
            _icon = new TrayIcon
            {
                // Set before the icon is attached: macOS refuses a null tooltip.
                ToolTipText = "ailocalpack",
                Icon = new WindowIcon(image),
                Menu = [_open, _check, new NativeMenuItemSeparator(), _quit],
            };
            _icon.Clicked += (_, _) => _window.Show();
            SetTexts();
            TrayIcon.SetIcons(app, [_icon]);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Couldn't create the tray icon");
            TrayAvailable = false;
        }

        main.Closing += (_, e) =>
        {
            if (CloseToTray.ShouldHide(e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic, IsOn, _quitting))
            {
                e.Cancel = true;
                _window.Hide();
                _logger.LogInformation("Window closed to the tray");
            }
        };
        if (app.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    _window.Show();
                }
            };
        }

        _settings.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        _localizer.CultureChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        Apply();
    }

    /// <summary>Quits for real (tray menu).</summary>
    public void Quit()
    {
        _quitting = true;
        _desktop?.Shutdown();
    }

    private void SetTexts()
    {
        _open!.Header = _localizer[nameof(Strings.Tray_Open)];
        _check!.Header = _localizer[nameof(Strings.Tray_CheckUpdates)];
        _quit!.Header = _localizer[nameof(Strings.Tray_Quit)];
    }

    private void Apply()
    {
        if (_desktop is null)
        {
            return;
        }

        var on = IsOn;
        if (_icon is not null)
        {
            _icon.IsVisible = on;
            SetTexts();
        }

        _desktop.ShutdownMode = on ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose;
        if (!on && _window.Window is { IsVisible: false })
        {
            // The tray went away while the window was hidden: bring it back rather than leave an invisible app.
            _window.Show();
        }
    }
}
