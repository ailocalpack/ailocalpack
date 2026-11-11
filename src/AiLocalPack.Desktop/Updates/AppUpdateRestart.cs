using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.Updates;

/// <summary>
/// <c>Restart to update</c> (Settings → About) and <c>Restart now</c> (the Activity feed) alike: refused while a pack is
/// being installed, asked first while a generation or a publish runs, else the app restarts into the new version.
/// </summary>
public sealed class AppUpdateRestart(AppUpdater updater, IDialogService dialogs, Localizer localizer)
{
    /// <summary>Why the app can't restart now (in the UI language); <c>null</c> when it restarts or asks first.</summary>
    public string? Request()
    {
        switch (updater.RestartBlock)
        {
            case AppRestartBlock.Installing:
                return localizer[nameof(Strings.Settings_About_Restart_Installing)];
            case AppRestartBlock.Working:
                dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
                    localizer[nameof(Strings.Settings_About_RestartConfirm_Title)],
                    localizer[nameof(Strings.Settings_About_RestartConfirm_Text)],
                    localizer[nameof(Strings.Settings_About_RestartConfirm_Confirm)],
                    () => updater.Restart(),
                    IsDestructive: false));
                return null;
            default:
                updater.Restart();
                return null;
        }
    }
}
