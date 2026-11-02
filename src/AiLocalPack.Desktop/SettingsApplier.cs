using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Localization;

namespace AiLocalPack.Desktop;

/// <summary>
/// Puts settings into effect: UI language and theme at startup and again on every change, so Settings applies
/// immediately without a restart. Started by <see cref="App"/> after the settings are loaded.
/// </summary>
public sealed class SettingsApplier(SettingsStore store, Localizer localizer, IAppTheming theming)
{
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Apply(store.Current);
        store.Changed += (_, settings) => Apply(settings);
    }

    private void Apply(AppSettings settings)
    {
        localizer.SetLanguage(settings.UiLanguage);
        theming.Apply(settings.Theme);
    }
}
