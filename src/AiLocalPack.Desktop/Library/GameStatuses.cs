using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;

namespace AiLocalPack.Desktop.Library;

/// <summary>
/// A game's <see cref="LocalizationStatus"/> for the "Translate games into" language (<see cref="AppSettings.GameLanguage"/>),
/// from the anti-cheat policy and the latest pack list. The library cards and the game page read the same answer;
/// installed packs (task 29), broken ones (task 33) and generation jobs (task 47) join here.
/// </summary>
public sealed class GameStatuses(AntiCheatPolicy policy, PackIndex packIndex, SettingsStore settings, IInstalledPacks installed, GenerationService jobs)
{
    /// <summary>Raised (on any thread) when a game's generation job started, moved on or ended: its status may have changed.</summary>
    public event EventHandler<GenerationJob>? JobChanged
    {
        add => jobs.Changed += value;
        remove => jobs.Changed -= value;
    }

    /// <summary>Raised (on any thread) when a game's installed pack changed: its status may have.</summary>
    public event EventHandler<GameId>? InstalledChanged
    {
        add => installed.Changed += value;
        remove => installed.Changed -= value;
    }

    /// <summary>The target language: an exact <see cref="GameLanguages.Tags"/> tag (setting, else the OS language).</summary>
    public string Language => GameLanguages.Resolve(settings.Current.GameLanguage, UiLanguages.Os);

    /// <summary>The pack list is the saved copy (registry unreachable).</summary>
    public bool IsStale => packIndex.Current is { IsStale: true };

    public LocalizationStatus Resolve(Game game) => Resolve(game, Language);

    /// <summary>Why install and generation refuse the game (<see cref="GameBlocks"/>); <c>null</c> when they don't.</summary>
    public GameBlock? ResolveBlock(Game game) => GameBlocks.Resolve(game, policy, packIndex.Current?.Index);

    public LocalizationStatus Resolve(Game game, string language)
    {
        ArgumentNullException.ThrowIfNull(game);
        return LocalizationStatuses.Resolve(game, language, policy.Resolve(game), packIndex.Current?.Index, installed.Find(game.Id, language), jobs.StateOf(game.Id), jobs.ResultStateOf(game.Id));
    }
}
