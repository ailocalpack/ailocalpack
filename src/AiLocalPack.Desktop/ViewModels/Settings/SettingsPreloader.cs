using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>
/// Prepares Settings at startup so its first visit opens without a pause: the language names (slow ICU data) load on
/// a background thread, then the section singletons are created on the UI thread. Best effort, never throws: a failure
/// is logged and the sections are simply built on the first visit, as before.
/// </summary>
internal sealed class SettingsPreloader(IServiceProvider services, IUiDispatcher ui, ILogger<SettingsPreloader> logger)
{
    public async Task PreloadAsync()
    {
        try
        {
            await Task.Run(LanguageOptions.Preload).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not preload language names");
        }

        ui.Post(() =>
        {
            try
            {
                _ = services.GetServices<SettingsSection>().Count();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not preload the settings sections");
            }
        });
    }
}
