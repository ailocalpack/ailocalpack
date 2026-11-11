using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Updates;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Startup;

/// <summary>
/// Runs what the app does on start (design §9) and reports it as <see cref="Progress"/>. Blocking steps (settings,
/// the saved game catalog) run off the UI thread before the library is shown, so the splash stays live; background
/// checks (Steam scan, engine and anti-cheat detection, the registry's pack list, then the re-check of installed packs
/// after game updates once both are in, the recovery of generation jobs a crash interrupted) run while the library is
/// in use. Never throws: a failed step
/// is logged and shown as a warning, and the app goes on with what it has.
/// </summary>
public sealed class AppStartup
{
    public const string SettingsStep = "settings";
    public const string RecoveryStep = "install-recovery";
    public const string CatalogStep = "catalog";
    public const string SteamStep = "steam";
    public const string DetectionStep = "detect";
    public const string IndexStep = "index";
    public const string PacksStep = "packs";
    public const string JobsStep = "jobs";
    public const string UpdatesStep = "updates";

    /// <summary>New games named in the Steam step's result; more are counted ("+3").</summary>
    private const int NamedNewGames = 3;

    private readonly SettingsStore _settings;
    private readonly SettingsApplier _applier;
    private readonly PackInstaller _installer;
    private readonly GameCatalog _catalog;
    private readonly GameLibrary _library;
    private readonly PackIndex _index;
    private readonly InstalledPackMonitor _monitor;
    private readonly GenerationService _jobs;
    private readonly AppUpdater _updates;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<AppStartup> _logger;
    private readonly StartupStep _settingsStep;
    private readonly StartupStep? _recoveryStep;
    private readonly StartupStep _catalogStep;
    private readonly StartupStep _steamStep;
    private readonly StartupStep _detectionStep;
    private readonly StartupStep _indexStep;
    private readonly StartupStep _packsStep;
    private readonly StartupStep _jobsStep;
    private readonly StartupStep _updatesStep;
    private bool _backgroundStarted;
    private bool _libraryDone;
    private bool _indexDone;

    public AppStartup(
        SettingsStore settings,
        SettingsApplier applier,
        PackInstaller installer,
        GameCatalog catalog,
        GameLibrary library,
        PackIndex index,
        InstalledPackMonitor monitor,
        GenerationService jobs,
        AppUpdater updates,
        Localizer localizer,
        IUiDispatcher ui,
        TimeProvider time,
        ILogger<AppStartup> logger)
    {
        _settings = settings;
        _applier = applier;
        _installer = installer;
        _catalog = catalog;
        _library = library;
        _index = index;
        _monitor = monitor;
        _jobs = jobs;
        _updates = updates;
        _localizer = localizer;
        _ui = ui;
        _logger = logger;
        _settingsStep = new(SettingsStep, StartupPhase.Blocking, nameof(Strings.Startup_Step_Settings), localizer, time);
        // Shown only when a journal survived a crash (design §9): one file check, cheap enough for the UI thread.
        _recoveryStep = installer.HasInterruptedTransaction
            ? new(RecoveryStep, StartupPhase.Blocking, nameof(Strings.Startup_Step_Recovery), localizer, time)
            : null;
        _catalogStep = new(CatalogStep, StartupPhase.Blocking, nameof(Strings.Startup_Step_Catalog), localizer, time);
        _steamStep = new(SteamStep, StartupPhase.Background, nameof(Strings.Startup_Step_Steam), localizer, time);
        _detectionStep = new(DetectionStep, StartupPhase.Background, nameof(Strings.Startup_Step_Detection), localizer, time);
        _indexStep = new(IndexStep, StartupPhase.Background, nameof(Strings.Startup_Step_Index), localizer, time);
        _packsStep = new(PacksStep, StartupPhase.Background, nameof(Strings.Startup_Step_Packs), localizer, time);
        _jobsStep = new(JobsStep, StartupPhase.Background, nameof(Strings.Startup_Step_Jobs), localizer, time);
        _updatesStep = new(UpdatesStep, StartupPhase.Background, nameof(Strings.Startup_Step_Updates), localizer, time);
        Progress = new StartupProgress(
            [_settingsStep, .. _recoveryStep is null ? Array.Empty<StartupStep>() : [_recoveryStep], _catalogStep, _steamStep, _detectionStep, _indexStep, _packsStep, _jobsStep, _updatesStep],
            localizer);
    }

    public StartupProgress Progress { get; }

    /// <summary>
    /// Loads settings (then applies language and theme), finishes an install a crash interrupted (task 27) and loads the
    /// saved game catalog, one after the other. Call it on
    /// the UI thread: the files are read on the thread pool, the steps change back on the caller's context.
    /// </summary>
    public async Task RunBlockingAsync()
    {
        await LoadSettingsAsync();
        await RecoverInstallAsync();
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
            t => _ui.Post(() =>
            {
                CompleteIndex(t.Result);
                _indexDone = true;
                StartPackCheck();
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        _jobsStep.Start();
        _ = Task.Run(_jobs.Recover).ContinueWith(
            t => _ui.Post(() =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    CompleteJobs(t.Result.Interrupted, t.Result.Open);
                }
                else
                {
                    _logger.LogError(t.Exception, "Checking the generation jobs failed");
                    Fail(_jobsStep);
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        StartUpdateCheck();
    }

    /// <summary>
    /// The app's own update (task 67): the step ends once the release feed answered (a newer version keeps downloading
    /// in the background); later checks run every few hours while the app runs.
    /// </summary>
    private void StartUpdateCheck()
    {
        _updatesStep.Start();
        _ = _updates.CheckAsync().ContinueWith(
            t => _ui.Post(() =>
            {
                var version = _updates.Version;
                switch (t.IsCompletedSuccessfully ? t.Result : AppUpdateStatus.Failed)
                {
                    case AppUpdateStatus.Off:
                        _updatesStep.Skip(() => _localizer[nameof(Strings.Startup_Updates_Off)]);
                        break;
                    case AppUpdateStatus.UpToDate:
                        _updatesStep.Complete(StartupTone.Ok, () => _localizer[nameof(Strings.Startup_Updates_Latest)]);
                        break;
                    case AppUpdateStatus.Downloading:
                        _updatesStep.Complete(StartupTone.Ok, () => _localizer.Format(nameof(Strings.Startup_Updates_Found), version ?? string.Empty));
                        break;
                    case AppUpdateStatus.Ready or AppUpdateStatus.Restarting:
                        _updatesStep.Complete(StartupTone.Ok, () => _localizer.Format(nameof(Strings.Startup_Updates_Ready), version ?? string.Empty));
                        break;
                    default:
                        _updatesStep.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Updates_Failed)]);
                        break;
                }

                _updates.StartSchedule();
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Crash recovery of generation jobs (task 47): a job left running when the app died is now interrupted; the
    /// Generation tab offers <c>Resume</c> (task 53).
    /// </summary>
    private void CompleteJobs(IReadOnlyList<GenerationJobEntry> interrupted, int open)
    {
        if (interrupted.Count > 0)
        {
            var names = string.Join(", ", interrupted.Select(e => e.Job.GameName).Distinct(StringComparer.Ordinal));
            _jobsStep.Complete(StartupTone.Warn, () => _localizer.Format(nameof(Strings.Startup_Jobs_Interrupted), names));
        }
        else if (open == 0)
        {
            _jobsStep.Skip(() => _localizer[nameof(Strings.Startup_Jobs_None)]);
        }
        else
        {
            _jobsStep.Complete(StartupTone.Ok, () => _localizer.Plural(nameof(Strings.Startup_Jobs_Done_One), open));
        }
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

    private async Task RecoverInstallAsync()
    {
        if (_recoveryStep is not { } step)
        {
            return;
        }

        step.Start(() => _localizer[nameof(Strings.Startup_Recovery_Running)]);
        try
        {
            var result = await Task.Run(() => _installer.Recover());
            switch (result.Outcome)
            {
                case RecoveryOutcome.RolledBack:
                    step.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Recovery_RolledBack)]);
                    break;
                case RecoveryOutcome.Completed:
                    step.Complete(StartupTone.Ok, () => _localizer[nameof(Strings.Startup_Recovery_Completed)]);
                    break;
                case RecoveryOutcome.UninstallFinished:
                    step.Complete(StartupTone.Ok, () => _localizer[nameof(Strings.Startup_Recovery_UninstallFinished)]);
                    break;
                case RecoveryOutcome.Failed:
                    step.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Recovery_Failed)]);
                    break;
                case RecoveryOutcome.Busy:
                    step.Skip(() => _localizer[nameof(Strings.Startup_Recovery_Busy)]);
                    break;
                case RecoveryOutcome.Nothing when result.BackupMoveFinished:
                    step.Complete(StartupTone.Ok, () => _localizer[nameof(Strings.Startup_Recovery_BackupMove)]);
                    break;
                default:
                    step.Skip(() => _localizer[nameof(Strings.Startup_Recovery_Nothing)]);
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Finishing an interrupted install at startup failed");
            Fail(step);
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
                _libraryDone = true;
                StartPackCheck();
                break;
            case GameLibraryRefreshFailed:
                foreach (var step in new[] { _steamStep, _detectionStep }.Where(s => !s.IsFinished))
                {
                    Fail(step);
                }

                _library.Progressed -= OnLibraryProgressed;
                _libraryDone = true;
                StartPackCheck();
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

    /// <summary>
    /// The re-check of installed packs (task 33) needs the games' new build IDs and the pack list (blocks, the installed
    /// revision's manifest), so it starts once the Steam scan and the pack list are both in.
    /// </summary>
    private void StartPackCheck()
    {
        if (!_libraryDone || !_indexDone || _packsStep.IsRunning || _packsStep.IsFinished)
        {
            return;
        }

        _packsStep.Start();
        _ = _monitor.CheckAllAsync().ContinueWith(
            t => _ui.Post(() =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    CompletePacks(t.Result);
                }
                else
                {
                    _logger.LogError(t.Exception, "Re-checking installed packs at startup failed");
                    Fail(_packsStep);
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>Packs needing the player come first (warning), then those re-applied, else how many are in place.</summary>
    private void CompletePacks(IReadOnlyList<PackRecheckResult> results)
    {
        if (results.Count == 0)
        {
            _packsStep.Skip(() => _localizer[nameof(Strings.Startup_Packs_None)]);
            return;
        }

        var attention = results.Where(r => r.Problem != InstalledPackProblem.None).Select(r => r.Game.Name).ToList();
        var reapplied = results.Where(r => r.Reapplied).Select(r => r.Game.Name).ToList();
        if (attention.Count > 0)
        {
            _packsStep.Complete(StartupTone.Warn, () => _localizer.Format(nameof(Strings.Startup_Packs_Attention), string.Join(", ", attention)));
        }
        else if (reapplied.Count > 0)
        {
            _packsStep.Complete(StartupTone.Ok, () => _localizer.Format(nameof(Strings.Startup_Packs_Reapplied), string.Join(", ", reapplied)));
        }
        else
        {
            _packsStep.Complete(StartupTone.Ok, () => _localizer.Plural(nameof(Strings.Startup_Packs_Done_One), results.Count));
        }
    }

    private void Fail(StartupStep step) => step.Complete(StartupTone.Warn, () => _localizer[nameof(Strings.Startup_Failed)]);
}
