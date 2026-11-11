using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;

namespace AiLocalPack.Desktop.Welcome;

/// <summary>
/// Opens the welcome tour (design §10): by itself the first time the main window is shown, and on request from <c>?</c>
/// in the chrome and Settings → About.
/// </summary>
public sealed class WelcomeTour(IDialogService dialogs, SettingsStore settings)
{
    /// <summary>
    /// Shows the tour unless it was shown before or another dialog is open (that one wins; the tour comes on a later start).
    /// Returns whether it was shown.
    /// </summary>
    public bool ShowOnFirstRun()
    {
        if (settings.Current.WelcomeTourShown || dialogs.Current is not null)
        {
            return false;
        }

        Show();
        return true;
    }

    /// <summary>Shows the tour from its first slide (an open tour starts over).</summary>
    public void Show() => dialogs.Show<WelcomeTourViewModel>();
}
