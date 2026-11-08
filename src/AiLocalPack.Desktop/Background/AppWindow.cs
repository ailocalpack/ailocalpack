using Avalonia.Controls;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Background;

/// <summary>The app's main window as background code sees it: whether the player is looking at it, and bringing it back.</summary>
public interface IAppWindow
{
    /// <summary>Shown and active (not closed to the tray, not minimized, no other app in front). Any thread.</summary>
    bool IsActive { get; }

    /// <summary>Shows the window (from the tray, minimized, behind) and activates it. Any thread.</summary>
    void Show();
}

/// <summary>Holds the <see cref="Views.MainWindow"/> once <c>App</c> made it; before that nothing is active.</summary>
public sealed class AppWindowHost : IAppWindow
{
    private Window? _window;
    private volatile bool _active;

    public bool IsActive => _active;

    public Window? Window => _window;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        window.Activated += (_, _) => _active = true;
        window.Deactivated += (_, _) => _active = false;
        window.Closed += (_, _) => _active = false;
    }

    public void Show() => Dispatcher.UIThread.Post(() =>
    {
        if (_window is not { } window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    });

    /// <summary>Hides the window to the tray (the app keeps running).</summary>
    public void Hide()
    {
        _active = false;
        _window?.Hide();
    }
}
