using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Startup;

/// <summary>
/// Runs what the app does on start (design §9) and reports it as <see cref="Progress"/>. Blocking steps (settings,
/// the saved game catalog) run off the UI thread before the library is shown, so the splash stays live; background
/// checks (Steam scan, engine and anti-cheat detection, the registry's pack list) run while the library is in use. Never throws: a failed step
/// is logged and shown as a warning, and the app goes on with what it has.
/// </summary>
public sealed class AppStartup
{
    public const string SettingsStep = "settings";
    public const string CatalogStep = "catalog";
    public const string SteamStep = "steam";
    public const string DetectionStep = "detect";
    public const string IndexStep = "index";

    /// <summary>New games named in the Steam step's result; more are counted ("+3").</summary>
    private const int NamedNewGames = 3;

    private readonly SettingsStore _settings;
    private readonly SettingsApplier _applier;
    private readonly GameCatalog _catalog;
    private readonly GameLibrary _library;
    private readonly PackIndex _index;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<AppStartup> _logger;
    private readonly StartupStep _settingsStep;
    private readonly StartupStep _catalogStep;
    private readonly StartupStep _steamStep;
    private readonly StartupStep _detectionStep;
    private readonly StartupStep _indexStep;
    private bool _backgroundStarted;

    public AppStartup(
        SettingsStore settings,
        SettingsApplier applier,
        GameCatalog catalog,
        GameLibrary library,
        PackIndex index,
        Localizer localizer,
        IUiDispatcher ui,
        TimeProvider time,
        ILogger<AppStartup> logger)
    {
        _settings = settings;
        _applier = applier;
        _catalog = catalog;
        _library = library;
        _index = index;
        _localizer = localizer;
        _ui = ui;
        _logger = logger;
        _settingsStep = new(SettingsStep, StartupPhase.Blocking, nameof(Strings.Startup_Step_Settings), localizer, time);
        _catalogStep = new(CatalogStep, StartupPhase.Blocking, nameof(Strings.Startup_Step_Catalog), localizer, time);
        _steamStep = new(SteamStep, StartupPhase.Background, nameof(Strings.Startup_Step_Steam), localizer, time);
        _detectionStep = new(DetectionStep, StartupPhase.Background, nameof(Strings.Startup_Step_Detection), localizer, time);
        _indexStep = new(IndexStep, StartupPhase.Background, nameof(Strings.Startup_Step_Index), localizer, time);
        Progress = new StartupProgress([_settingsStep, _catalogStep, _steamStep, _detectionStep, _indexStep], localizer);
    }

    public StartupProgress Progress { get; }

    /// <summary>
    /// Loads settings (then applies language and theme) and the saved game catalog, one after the other. Call it on
    /// the UI thread: the files are read on the thread pool, the steps change back on the caller's context.
    /// </summary>
    public async Task RunBlockingAsync()
    {
        await LoadSettingsAsync();
        await LoadCatalogAsync();
    }

    /// <summary>Starts the background checks once (later calls do nothing); their progress arrives through <see cref="IUiDispatcher"/>.</summary>
    public void StartBackground()
    {
        if (_backgroundStarted)
        {
            return;
        }

        _backgroundStarted = true;
        _steamStep.Start();
        _library.Progressed += OnLibraryProgressed;
        _ = _library.RefreshAsync();
        _indexStep.Start();
        _ = _index.RefreshAsync().ContinueWith(
            t => _ui.Post(() => CompleteIndex(t.Result)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private async Task LoadSettingsAsync()
    {
        _settingsStep.Start();
        try
        {
            var (settings, language) = await Task.Run(() =>
            {
                var loaded = _settings.Load();
                // Native names read ICU data (slow the first time), so the name is looked up here, off the UI thread.
                return (loaded, LanguageNames.Native(UiLanguages.Resolve(loaded.UiLanguage, UiLanguages.Os).TwoLetterISOLanguageName));
            });
            _applier.Start();
            var themeKey = settings.Theme switch
            {
                AppTheme.Dark => nameof(Strings.Startup_Settings_ThemeDark),
                AppTheme.Light => nameof(Strings.Startup_Settings_ThemeLight),
                _ => nameof(Strings.Startup_Settings_ThemeSystem),
            };
            _settingsStep.Complete(StartupTone.Ok, () => _localizer.Format(nameof(Strings.Startup_Settings_Done), language, _localizer[themeKey]));
        }
        catch (Exception e)
        {
            // Settings never stop the app: the defaults stay in effect.
            _logger.LogError(e, "Loading settings at startup failed");
            Fail(_settingsStep);
            _applier.Start();
        }
    }

    private async Task LoadCatalogAsync()
    {
        _catalogStep.Start();
        try
        {
            var games = await Task.Run(_catalog.Load);
            switch (_catalog.LoadOutcome)
            {
                case GameCatalogLoadOutcome.Missing:
                    _catalogStep.Skip(() => _localizer[nameof(Strings.Startup_Catalog_Missing)]);
                    break;
                case GameCatalogLoadOutcome.Corrupted:
                    _catalogStep.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Catalog_Corrupted)]);
                    break;
                case GameCatalogLoadOutcome.Unreadable:
                    _catalogStep.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Catalog_Unreadable)]);
                    break;
                default:
                    _catalogStep.Complete(StartupTone.Ok, () => _localizer.Plural(nameof(Strings.Library_GameCount_One), games.Count));
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Loading the game catalog at startup failed");
            Fail(_catalogStep);
        }
    }

    private void OnLibraryProgressed(object? sender, GameLibraryProgress progress) => _ui.Post(() => Apply(progress));

    private void Apply(GameLibraryProgress progress)
    {
        switch (progress)
        {
            case GameLibraryScanned scanned when _steamStep.IsRunning:
                CompleteSteam(scanned);
                _detectionStep.Start();
                break;
            case GameLibraryDetected detected when _detectionStep.IsRunning:
                if (detected.Games == 0)
                {
                    _detectionStep.Skip(() => _localizer[nameof(Strings.Startup_Detection_None)]);
                }
                else
                {
                    _detectionStep.Complete(StartupTone.Ok, () => _localizer.Plural(nameof(Strings.Startup_Detection_Done_One), detected.Games));
                }

                _library.Progressed -= OnLibraryProgressed;
                break;
            case GameLibraryRefreshFailed:
                foreach (var step in new[] { _steamStep, _detectionStep }.Where(s => !s.IsFinished))
                {
                    Fail(step);
                }

                _library.Progressed -= OnLibraryProgressed;
                break;
        }
    }

    private void CompleteSteam(GameLibraryScanned scanned)
    {
        var scan = scanned.Scan;
        if (scan.Libraries.Count == 0)
        {
            _steamStep.Skip(() => _localizer[nameof(Strings.Startup_Steam_NotFound)]);
            return;
        }

        var added = scanned.Changes.OfType<GameAdded>().Select(c => c.Game.Name).ToList();
        // On a first launch (or a rebuilt catalog) every game is new: just the count then.
        var named = added.Count > 0 && added.Count < scan.Games.Count;
        _steamStep.Complete(StartupTone.Ok, () =>
        {
            var games = _localizer.Plural(nameof(Strings.Library_GameCount_One), scan.Games.Count);
            if (!named)
            {
                return games;
            }

            var names = string.Join(", ", added.Take(NamedNewGames));
            if (added.Count > NamedNewGames)
            {
                names = _localizer.Format(nameof(Strings.Startup_Steam_More), names, added.Count - NamedNewGames);
            }

            return _localizer.Format(nameof(Strings.Startup_Steam_New), games, names);
        });
    }

    private void CompleteIndex(RegistryIndexSnapshot snapshot)
    {
        switch (snapshot)
        {
            case { IsFresh: true, Index: { } index }:
                _indexStep.Complete(StartupTone.Ok, () => _localizer.Plural(nameof(Strings.Startup_Index_Done_One), index.PackCount));
                break;
            case { IsStale: true, FetchedAt: { } savedAt }:
                _indexStep.Complete(StartupTone.Warn, () => _localizer.Format(
                    nameof(Strings.Startup_Index_Stale), savedAt.ToLocalTime().ToString("g", _localizer.Culture)));
                break;
            case { Problem: RegistryIndexProblem.NewerFormat }:
                _indexStep.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Index_NewerFormat)]);
                break;
            case { Problem: RegistryIndexProblem.InvalidLocation }:
                _indexStep.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Index_BadAddress)]);
                break;
            default:
                _indexStep.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Index_Unavailable)]);
                break;
        }
    }

    private void Fail(StartupStep step) => step.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Failed)]);
}
