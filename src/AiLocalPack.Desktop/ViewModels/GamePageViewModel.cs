using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Game page, design §3: a hero header (backdrop from the cover's colors, cover, name, fact chips engine / store /
/// version, the installed pack block, the state's primary action with its caption), the update banner, and the tabs
/// Packs (count), Generation (% while a job runs), Audio and Textures (coming soon). The tab bar sticks to the top with
/// a mini cover, the name and the primary action once the header scrolls away (<see cref="IsTabBarStuck"/>, set by the
/// view). Follows the catalog (rename, drive unplugged), the pack list and the target language while open.
/// A blocked game (design §3a: anti-cheat, studio opt-out) keeps the header with a grey cover and no action, and shows
/// the red explanation with <c>Report</c> instead of the tabs.
/// </summary>
public sealed partial class GamePageViewModel : PageViewModel
{
    private readonly GameLibrary _library;
    private readonly GameStatuses _statuses;
    private readonly PackIndex _packIndex;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ISystemShell _shell;
    private readonly AntiCheatDetector _antiCheats;
    private readonly GameSessions _sessions;
    private readonly InstallFlow _install;
    private readonly InstalledPackMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closed = new();
    private IBrush? _placeholderBrush;
    private IBrush? _greyPlaceholderBrush;
    private (IBrush Base, IBrush Glow, IBrush SecondGlow, bool Grey)? _backdrop;

    /// <summary>Play was pressed and the launch (finding the executable, asking the OS) hasn't answered yet.</summary>
    private bool _launching;

    /// <summary>Why the last Play failed: the line under the caption; empty when it didn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchError))]
    private string _launchError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Cover), nameof(HasCover), nameof(Icon), nameof(HasIcon))]
    private CoverImage? _image;

    /// <summary>A grey copy of <see cref="Image"/>'s bitmap, shown while the game is blocked.</summary>
    private Bitmap? _greyImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPacksTab), nameof(IsGenerationTab), nameof(HasPrimaryAction), nameof(IsSoonTab), nameof(SoonTitle), nameof(SoonText), nameof(SoonIcon))]
    private GamePageTab _selectedTab;

    /// <summary>The header scrolled out of view: the sticky bar shows the mini cover, name and primary action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStickyBar))]
    private bool _isTabBarStuck;

    /// <param name="initialTab">The tab to open; <see cref="GamePageTab.Default"/> = Generation while a job runs, else Packs.</param>
    public GamePageViewModel(
        Game game,
        GameLibrary library,
        GameStatuses statuses,
        PackIndex packIndex,
        SettingsStore settings,
        Localizer localizer,
        IUiDispatcher ui,
        ICoverLoader covers,
        ISystemShell shell,
        AntiCheatDetector antiCheats,
        PackManifests manifests,
        GameSessions sessions,
        InstallFlow install,
        InstalledPackMonitor monitor,
        TimeProvider time,
        GenerationService jobs,
        IGenerationEstimates estimates,
        AgentsSettingsSection agents,
        GamePageTab initialTab = GamePageTab.Default,
        ILogger<GamePageViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        Game = game;
        _library = library;
        _statuses = statuses;
        _packIndex = packIndex;
        _settings = settings;
        _localizer = localizer;
        _ui = ui;
        _shell = shell;
        _antiCheats = antiCheats;
        _sessions = sessions;
        _install = install;
        _monitor = monitor;
        _time = time;
        _logger = logger ?? NullLogger<GamePageViewModel>.Instance;
        Tabs = [.. new[] { GamePageTab.Packs, GamePageTab.Generation, GamePageTab.Audio, GamePageTab.Textures }
            .Select(t => new GameTabViewModel(t, tab => SelectedTab = tab))];
        PrimaryCommand = new RelayCommand(RunPrimary, () => CanRunPrimary);
        Status = statuses.Resolve(game);
        Action = GamePrimaryActions.Resolve(Status);
        SelectedTab = initialTab != GamePageTab.Default ? initialTab
            : GamePrimaryActions.OpensOnGeneration(Status) ? GamePageTab.Generation
            : GamePageTab.Packs;
        UpdateTabs();
        Packs = new PacksTabViewModel(game, Status, Language, packIndex, manifests, localizer, shell, OpenGeneration, _logger);
        Packs.InstallRequested += OnInstallRequested;
        Packs.UninstallRequested += OnUninstallRequested;
        Packs.SetInstallState(_install.IsBusy, InstallingPackId);
        Generation = new GenerationTabViewModel(game, Language, jobs, estimates, agents, settings, packIndex, localizer, ui, _logger);
        ActivateTab();

        _library.Catalog.Changed += OnCatalogChanged;
        _packIndex.Changed += OnPackIndexChanged;
        _settings.Changed += OnSettingsChanged;
        _localizer.CultureChanged += OnCultureChanged;
        _sessions.Changed += OnSessionChanged;
        _install.Changed += OnInstallChanged;
        _statuses.InstalledChanged += OnInstalledChanged;
        _statuses.JobChanged += OnJobChanged;
        _ = LoadCoverAsync(covers);
    }

    public Game Game { get; private set; }

    public override string Title => Game.Name;

    public string Name => Game.Name;

    /// <summary>The game's status for <see cref="Language"/>.</summary>
    public LocalizationStatus Status { get; private set; }

    /// <summary>The header's primary action for <see cref="Status"/>.</summary>
    public GamePrimaryAction Action { get; private set; }

    /// <summary>The target language (exact tag) the page is for.</summary>
    public string Language => _statuses.Language;

    public IReadOnlyList<GameTabViewModel> Tabs { get; }

    /// <summary>The Packs tab (design §4): list, details, language and sort.</summary>
    public PacksTabViewModel Packs { get; }

    /// <summary>The Generation tab (design §6): the start form, then the job's progress.</summary>
    public GenerationTabViewModel Generation { get; }

    // ---------- Cover and backdrop ----------

    /// <summary>The cover art; grey while the game is blocked.</summary>
    public Bitmap? Cover => Image is { IsIcon: false } image ? Shown(image) : null;

    public bool HasCover => Cover is not null;

    /// <summary>The game's icon on the placeholder; grey while the game is blocked.</summary>
    public Bitmap? Icon => Image is { IsIcon: true } image ? Shown(image) : null;

    public bool HasIcon => Icon is not null;

    /// <summary>The colors the backdrop is drawn from: the cover's, else the placeholder's (until the cover loads).</summary>
    public CoverColors BackdropColors { get; private set; }

    /// <summary>Made on first read (UI thread), like the library's placeholder brush; grey while the game is blocked.</summary>
    public IBrush PlaceholderBrush => IsBlocked
        ? _greyPlaceholderBrush ??= PlaceholderColors.CreateBrush(Game.Id, greyscale: true)
        : _placeholderBrush ??= PlaceholderColors.CreateBrush(Game.Id);

    /// <summary>The backdrop's base: the cover's dominant color, darkened.</summary>
    public IBrush BackdropBrush => Backdrop.Base;

    /// <summary>A soft radial glow at the top left, in the cover's most vivid color.</summary>
    public IBrush BackdropGlowBrush => Backdrop.Glow;

    /// <summary>A second glow at the top right, in another color of the cover.</summary>
    public IBrush BackdropSecondGlowBrush => Backdrop.SecondGlow;

    /// <summary>A blocked game's backdrop is grey like its cover.</summary>
    private (IBrush Base, IBrush Glow, IBrush SecondGlow, bool Grey) Backdrop =>
        _backdrop is { } backdrop && backdrop.Grey == IsBlocked ? backdrop : (_backdrop = CreateBackdrop(BackdropColors, IsBlocked)).Value;

    // ---------- Fact chips ----------

    /// <summary>"Unity (Mono)", "Engine unknown"; empty while not detected (chip hidden).</summary>
    public string EngineText => !EngineKind.TryParse(Game.Engine, out var engine) ? string.Empty
        : engine.Engine == GameEngine.Unknown ? _localizer[nameof(Strings.Game_Chip_EngineUnknown)]
        : engine.DisplayName;

    public bool HasEngine => EngineText.Length > 0;

    /// <summary>"Steam" or "Added manually".</summary>
    public string StoreText => _localizer[Game.Source == GameSource.Steam ? nameof(Strings.Game_Chip_Steam) : nameof(Strings.Game_Chip_Manual)];

    public bool IsSteam => Game.Source == GameSource.Steam;

    /// <summary>"build 14 882 031" (Steam build ID, digits grouped) or "files hash 1a2b3c4d"; empty when unknown.</summary>
    public string VersionText => Game.Version switch
    {
        { BuildId: { } build } => _localizer.Format(
            nameof(Strings.Game_Chip_Build),
            long.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number.ToString("N0", _localizer.Culture) : build),
        { FileHashes: { } files } => _localizer.Format(nameof(Strings.Game_Chip_FilesHash), ShortHash(files)),
        _ => string.Empty,
    };

    public bool HasVersion => VersionText.Length > 0;

    /// <summary>What the version chip means: "Game version: Steam build ID" / "Game version: hash of 12 files".</summary>
    public string VersionTip => Game.Version is { FileHashes: { } hashes }
        ? _localizer.Plural(nameof(Strings.Game_Chip_FilesHash_Tip_One), hashes.Count)
        : _localizer[nameof(Strings.Game_Chip_Build_Tip)];

    // ---------- Primary action ----------

    /// <summary>None on the Generation tab's start form (design §6: its own button starts the job).</summary>
    public bool HasPrimaryAction => Action.Kind != GamePrimaryActionKind.None
        && !(Action.Kind == GamePrimaryActionKind.Generate && IsGenerationTab);

    /// <summary>
    /// "Install best pack", "Generate translation with AI", "Play", "Update to rev 4", "View progress"…; Play reads
    /// "Starting…" from the press until the game's process shows up, then "Running" until it ends.
    /// </summary>
    public string PrimaryText => PlayPhase switch
    {
        GameSessionPhase.Starting => _localizer[nameof(Strings.Game_Action_Starting)],
        GameSessionPhase.Running => _localizer[nameof(Strings.Game_Action_Running)],
        _ => PrimaryActionTexts.Label(Action, _localizer),
    };

    /// <summary>What exactly happens (<see cref="PrimaryActionTexts.Caption"/>); "Running since 21:04" while the game runs.</summary>
    public string PrimaryCaption => PlayPhase == GameSessionPhase.Running && Session?.StartedAt is { } started
        ? _localizer.Format(nameof(Strings.Game_Caption_RunningSince), TimeZoneInfo.ConvertTime(started, _time.LocalTimeZone).ToString("t", _localizer.Culture))
        : PrimaryActionTexts.Caption(Action, Game, _localizer);

    public bool HasLaunchError => LaunchError.Length > 0;

    /// <summary>The game launched from the app, while it is starting or running.</summary>
    private GameSessionState? Session => _sessions.Find(Game.Id);

    /// <summary>Where the launched game is, shown only on the Play action; <c>null</c> when it isn't launched.</summary>
    private GameSessionPhase? PlayPhase => Action.Kind != GamePrimaryActionKind.Play ? null
        : _launching ? GameSessionPhase.Starting
        : Session?.Phase;

    /// <summary>Resource key of the button's icon.</summary>
    public string PrimaryIcon => Action.Kind switch
    {
        GamePrimaryActionKind.InstallBestPack => "IconDownload",
        GamePrimaryActionKind.Play => "IconPlay",
        GamePrimaryActionKind.UpdatePack or GamePrimaryActionKind.Reapply => "IconRefresh",
        GamePrimaryActionKind.ViewProgress => "IconLoader",
        _ => "IconSparkles",
    };

    /// <summary>View progress is teal (generation), the others the accent.</summary>
    public bool IsPrimaryGen => Action.Kind == GamePrimaryActionKind.ViewProgress;

    /// <summary>
    /// Not while the game is starting or running (Play), nor for an unreachable folder, nor while an install, re-apply or
    /// uninstall runs (one at a time, design §4a).
    /// </summary>
    public bool CanRunPrimary => Action.Kind != GamePrimaryActionKind.None && PlayPhase is null && Game.IsAvailable && !_install.IsBusy;

    public IRelayCommand PrimaryCommand { get; }

    // ---------- Installed pack block and update banner ----------

    /// <summary>The installed pack, or the progress of an operation on this game (installing, re-applying, uninstalling).</summary>
    public bool ShowInstalled => (Status.Installed is not null || IsOperating) && !Status.IsBlocked;

    /// <summary>The operation running on this game, if any.</summary>
    public InstallOperation? Operation => _install.Current is { } operation && operation.Game == Game.Id ? operation : null;

    /// <summary>The installed block shows a spinner, "Installing «…» · step N of M", the step and a bar.</summary>
    public bool IsOperating => Operation is not null;

    /// <summary>The installed pack's line, while nothing runs on the game.</summary>
    public bool ShowInstalledPack => Status.Installed is not null && !IsOperating;

    /// <summary>"Installing «Фонарщики» · step 2 of 6".</summary>
    public string OperationTitle => Operation is { } operation ? InstallTexts.Title(operation, _localizer) : string.Empty;

    /// <summary>"Rebuilding game files from the strings".</summary>
    public string OperationStep => Operation is { } operation ? InstallTexts.Step(operation.Step, _localizer) : string.Empty;

    /// <summary>The whole operation, 0–100, for the bar.</summary>
    public double OperationPercent => Operation?.Percent ?? 0;

    /// <summary>The ⋯ menu's items are off while any operation runs.</summary>
    public bool CanChangeInstalled => !_install.IsBusy && Status.Installed is not null && Game.IsAvailable;

    /// <summary>The pack being installed here, for the Packs tab's <c>installing</c> tag.</summary>
    private string? InstallingPackId => Operation is { Kind: InstallOperationKind.Install } operation ? operation.PackId : null;

    /// <summary>A game update broke the installed pack: the block is amber with ⚠.</summary>
    public bool IsInstalledBroken => Status.Installed is { BrokenByUpdate: true };

    public string InstalledLabel => _localizer[Status.Installed?.Problem switch
    {
        InstalledPackProblem.Incompatible => nameof(Strings.Game_Installed_Broken),
        InstalledPackProblem.Wiped or InstalledPackProblem.ReapplyFailed => nameof(Strings.Game_Installed_Wiped),
        _ => nameof(Strings.Game_Installed_Label),
    }];

    /// <summary>"«Фонарщики» · alice · rev 4" (title and author from the registry, the pack ID when it's gone from it).</summary>
    public string InstalledText => Status.Installed is { } installed
        ? InstalledPack is { } pack
            ? _localizer.Format(nameof(Strings.Game_Installed_Text), pack.Title, pack.Author.GitHub, installed.Revision)
            : _localizer.Format(nameof(Strings.Game_Installed_TextNoAuthor), installed.PackId, installed.Revision)
        : string.Empty;

    /// <summary>The amber banner between header and tabs when an update broke the installed pack.</summary>
    public bool ShowUpdateBanner => Status.Kind == LocalizationStatusKind.NeedsUpdate;

    /// <summary>
    /// Incompatible: "The game updated (build N). The installed «…» rev 3 is incompatible…"; files put back and not
    /// re-applied: "The game update put back the original files…"; the automatic re-apply failed: why.
    /// </summary>
    public string UpdateBannerText => Status.Installed switch
    {
        { Problem: InstalledPackProblem.Wiped } installed => _localizer.Format(nameof(Strings.Game_UpdateBanner_Wiped), InstalledTitle, installed.Revision),
        { Problem: InstalledPackProblem.ReapplyFailed } installed => _localizer.Format(
            nameof(Strings.Game_UpdateBanner_ReapplyFailed),
            InstalledTitle,
            installed.Revision,
            ReapplyFailure?.Error is { } error ? InstallTexts.Reason(error, _localizer) : string.Empty),
        { } installed => _localizer.Format(
            nameof(Strings.Game_UpdateBanner),
            VersionText,
            InstalledTitle,
            installed.Revision),
        _ => string.Empty,
    };

    /// <summary>The banner's <c>Translate new strings</c>: only when the header offers the update (else it is the header's action).</summary>
    public bool ShowBannerTranslate => Action.Kind == GamePrimaryActionKind.UpdatePack && Status.Installed is { NeedsReapply: false };

    /// <summary>The banner's <c>Re-apply</c> for a pack that still fits, when the header offers the newer revision instead.</summary>
    public bool ShowBannerReapply => Action.Kind == GamePrimaryActionKind.UpdatePack && Status.Installed is { NeedsReapply: true };

    /// <summary>The automatic re-apply that failed (the banner's <c>Details</c>).</summary>
    private InstallOutcome? ReapplyFailure => Status.Installed is { Problem: InstalledPackProblem.ReapplyFailed } ? _monitor.Find(Game.Id)?.Failure : null;

    public bool ShowBannerDetails => ReapplyFailure is not null;

    /// <summary>The banner's <c>Details</c>: the failed re-apply's reason and log.</summary>
    [RelayCommand]
    private void ShowReapplyDetails()
    {
        if (ReapplyFailure is { } outcome)
        {
            _install.ShowDetails(outcome);
        }
    }

    // ---------- Blocked game (design §3a) ----------

    /// <summary>Anti-cheat or studio opt-out: the red explanation replaces the tabs.</summary>
    public bool IsBlocked => Status.IsBlocked;

    /// <summary>The tab bar and the tab: hidden for a blocked game.</summary>
    public bool ShowTabs => !IsBlocked;

    /// <summary>The sticky copy of the tab bar: once the in-flow one scrolled away, never for a blocked game.</summary>
    public bool ShowStickyBar => IsTabBarStuck && !IsBlocked;

    /// <summary>"Easy Anti-Cheat was detected. Modifying game files can get your account banned…" or the opt-out text.</summary>
    public string BlockedText => Status.Block switch
    {
        LocalizationBlock.OptOut => _localizer[nameof(Strings.Game_Blocked_OptOut)],
        LocalizationBlock.AntiCheat => _localizer.Format(nameof(Strings.Game_Blocked_AntiCheat), Status.AntiCheatName ?? string.Empty),
        _ => string.Empty,
    };

    /// <summary>
    /// <c>Report</c>: a new registry issue in the browser, filled in with the game, its ID, the reason and the anti-cheat
    /// file found in the folder (looked up again off the UI thread: the catalog keeps only the anti-cheat ID).
    /// </summary>
    [RelayCommand]
    private async Task ReportAsync()
    {
        if (_statuses.ResolveBlock(Game) is not { } block)
        {
            return;
        }

        string? detectedFile = null;
        if (block.Reason == LocalizationBlock.AntiCheat && Game.IsAvailable)
        {
            var path = Game.InstallPath;
            var detection = await Task.Run(() => _antiCheats.Detect(path));
            detectedFile = (detection.Matches.FirstOrDefault(m => m.Id == block.AntiCheatId) ?? detection.Match)?.Marker;
        }

        if (!_shell.OpenUrl(BlockReport.IssueUri(Game, block, detectedFile, AppVersion.Current)))
        {
            _logger.LogWarning("Couldn't open the block report for {Game}", Game.Id);
        }
    }

    // ---------- Tabs ----------

    public bool IsPacksTab => SelectedTab == GamePageTab.Packs;

    public bool IsGenerationTab => SelectedTab == GamePageTab.Generation;

    /// <summary>Audio or Textures: the coming-soon illustration.</summary>
    public bool IsSoonTab => SelectedTab is GamePageTab.Audio or GamePageTab.Textures;

    public string SoonTitle => SelectedTab == GamePageTab.Textures
        ? _localizer[nameof(Strings.Game_Soon_TexturesTitle)]
        : _localizer[nameof(Strings.Game_Soon_AudioTitle)];

    public string SoonText => SelectedTab == GamePageTab.Textures
        ? _localizer[nameof(Strings.Game_Soon_TexturesText)]
        : _localizer.Format(nameof(Strings.Game_Soon_AudioText), LanguageName);

    public string SoonIcon => SelectedTab == GamePageTab.Textures ? "IconImage" : "IconMusic";

    /// <summary>The pack list isn't known (first fetch running, registry never answered).</summary>
    public bool IsStatusUnknown => Status.Kind == LocalizationStatusKind.Unknown;

    /// <summary>The target language in its own name ("Русский").</summary>
    public string LanguageName => LanguageNames.Native(Language);

    private RegistryPack? InstalledPack => Status.Installed is { } installed ? Status.Packs.FirstOrDefault(p => p.Id == installed.PackId) : null;

    private string InstalledTitle => InstalledPack?.Title ?? Status.Installed?.PackId ?? string.Empty;

    /// <summary>The page shows <paramref name="other"/> when it is for the same game (whatever the tab).</summary>
    public override bool IsSamePage(PageViewModel other) => other is GamePageViewModel page && page.Game.Id == Game.Id;

    public override void OnClosed()
    {
        _library.Catalog.Changed -= OnCatalogChanged;
        _packIndex.Changed -= OnPackIndexChanged;
        _settings.Changed -= OnSettingsChanged;
        _localizer.CultureChanged -= OnCultureChanged;
        _sessions.Changed -= OnSessionChanged;
        _install.Changed -= OnInstallChanged;
        _statuses.InstalledChanged -= OnInstalledChanged;
        _statuses.JobChanged -= OnJobChanged;
        _closed.Cancel();
        Packs.Close();
        Generation.Close();
    }

    /// <summary>⋯ → <c>Re-apply</c>: writes the installed pack's files again (after a game update or a file check reverted them).</summary>
    [RelayCommand(CanExecute = nameof(CanChangeInstalled))]
    private Task ReapplyAsync() => _install.ReapplyAsync(Game);

    /// <summary>⋯ → <c>Show in Packs</c>: the Packs tab on the installed pack.</summary>
    [RelayCommand]
    private void ShowInPacks()
    {
        SelectedTab = GamePageTab.Packs;
        if (Status.Installed is { } installed)
        {
            Packs.ShowPack(Language, installed.PackId);
        }
    }

    /// <summary>⋯ → <c>Uninstall (restore originals)</c>: asks, then restores the original files.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeInstalled))]
    private void Uninstall() => _install.Uninstall(Game);

    /// <summary>"Generate with AI" on the empty Packs tab, "Translate new strings" on the banner: the Generation tab.</summary>
    [RelayCommand]
    private void OpenGeneration() => SelectedTab = GamePageTab.Generation;

    /// <summary>
    /// Play launches the game (<see cref="GameSessions"/>); Install installs the best compatible pack (the install flow;
    /// when none can be installed the Packs tab says why); Update installs the registry's later revision of the installed
    /// pack (the install flow checks it and asks when a risk applies) and Re-apply writes the installed one again (task 33);
    /// Generate, Translate new strings and View progress open the Generation tab (update mode comes with task 56).
    /// </summary>
    private void RunPrimary()
    {
        if (Action.Kind == GamePrimaryActionKind.Play)
        {
            _ = PlayAsync();
            return;
        }

        if (Action.Kind == GamePrimaryActionKind.InstallBestPack)
        {
            _ = _install.InstallBestAsync(Game, () => SelectedTab = GamePageTab.Packs);
            return;
        }

        if (Action is { Kind: GamePrimaryActionKind.UpdatePack, Pack: { } newer })
        {
            _ = _install.InstallAsync(Game, newer);
            return;
        }

        if (Action.Kind == GamePrimaryActionKind.Reapply)
        {
            _ = _install.ReapplyAsync(Game);
            return;
        }

        SelectedTab = Action.Kind switch
        {
            GamePrimaryActionKind.Generate or GamePrimaryActionKind.TranslateNewStrings or GamePrimaryActionKind.ViewProgress => GamePageTab.Generation,
            _ => SelectedTab,
        };
    }

    private async Task PlayAsync()
    {
        _launching = true;
        LaunchError = string.Empty;
        RaisePlayState();
        GameLaunchResult result;
        try
        {
            result = await _sessions.LaunchAsync(Game);
        }
        finally
        {
            _launching = false;
        }

        _ui.Post(() =>
        {
            LaunchError = LaunchTexts.Problem(result, _localizer);
            RaisePlayState();
        });
    }

    private void OnInstallRequested(object? sender, PackRowViewModel row) =>
        _ = _install.InstallAsync(Game, row.Pack, row.Manifest, row.Compatibility);

    private void OnUninstallRequested(object? sender, EventArgs e) => _install.Uninstall(Game);

    /// <summary>An operation started, moved on or ended (any game: the buttons are off meanwhile). Raised on the UI thread.</summary>
    private void OnInstallChanged(object? sender, EventArgs e)
    {
        Packs.SetInstallState(_install.IsBusy, InstallingPackId);
        OnPropertyChanged(nameof(ShowInstalled));
        OnPropertyChanged(nameof(ShowInstalledPack));
        OnPropertyChanged(nameof(Operation));
        OnPropertyChanged(nameof(IsOperating));
        OnPropertyChanged(nameof(OperationTitle));
        OnPropertyChanged(nameof(OperationStep));
        OnPropertyChanged(nameof(OperationPercent));
        OnPropertyChanged(nameof(CanChangeInstalled));
        OnPropertyChanged(nameof(CanRunPrimary));
        PrimaryCommand.NotifyCanExecuteChanged();
        ReapplyCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private void OnInstalledChanged(object? sender, GameId game)
    {
        if (game == Game.Id)
        {
            _ui.Post(Refresh);
        }
    }

    /// <summary>The game's generation job started, moved on or ended (any thread): status, tab % and the tab's card follow.</summary>
    private void OnJobChanged(object? sender, Core.Generation.GenerationJob job)
    {
        if (job.Game == Game.Id)
        {
            _ui.Post(Refresh);
        }
    }

    private void OnSessionChanged(object? sender, GameSessionEvent change)
    {
        if (change.Game == Game.Id)
        {
            _ui.Post(RaisePlayState);
        }
    }

    private void RaisePlayState()
    {
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(PrimaryCaption));
        OnPropertyChanged(nameof(CanRunPrimary));
        PrimaryCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTabChanged(GamePageTab value)
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Tab == value;
        }

        ActivateTab();
    }

    /// <summary>
    /// The Packs tab fetches manifests only once shown, the Generation tab estimates the game and checks the agents (never
    /// for a blocked game).
    /// </summary>
    private void ActivateTab()
    {
        if (SelectedTab == GamePageTab.Packs && !IsBlocked && Packs is not null)
        {
            Packs.Activate();
        }

        if (SelectedTab == GamePageTab.Generation && !IsBlocked && Generation is not null)
        {
            Generation.Activate();
        }
    }

    private void OnCatalogChanged(object? sender, GameCatalogChangedEventArgs e) => _ui.Post(() =>
    {
        if (e.Games.FirstOrDefault(g => g.Id == Game.Id) is { } game && game != Game)
        {
            Game = game;
            Refresh();
        }
    });

    private void OnPackIndexChanged(object? sender, RegistryIndexSnapshot snapshot) => _ui.Post(Refresh);

    private void OnSettingsChanged(object? sender, AppSettings settings) => _ui.Post(Refresh);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        Refresh();
        Packs.RefreshTexts();
        Generation.RefreshTexts();
    }

    /// <summary>Recomputes the status and action and re-raises every text (catalog, pack list, language, UI language).</summary>
    private void Refresh()
    {
        Status = _statuses.Resolve(Game);
        Action = GamePrimaryActions.Resolve(Status);
        UpdateTabs();
        Packs.Update(Game, Status, Language);
        Generation.Update(Game, Language);
        ActivateTab();
        OnPropertyChanged(string.Empty);
        PrimaryCommand.NotifyCanExecuteChanged();
        ReapplyCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTabs()
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Tab == SelectedTab;
            tab.SoonText = _localizer[nameof(Strings.Game_Tab_Soon)];
            (tab.Label, tab.Counter) = tab.Tab switch
            {
                GamePageTab.Packs => (_localizer[nameof(Strings.Game_Tab_Packs)], Status.Packs.Count > 0 ? Status.Packs.Count.ToString("N0", _localizer.Culture) : string.Empty),
                GamePageTab.Generation => (_localizer[nameof(Strings.Game_Tab_Generation)], Status.Job is { } job ? _localizer.Format(nameof(Strings.Game_Tab_Percent), Math.Clamp(job.Percent, 0, 100)) : string.Empty),
                GamePageTab.Audio => (_localizer[nameof(Strings.Game_Tab_Audio)], string.Empty),
                _ => (_localizer[nameof(Strings.Game_Tab_Textures)], string.Empty),
            };
        }
    }

    private Bitmap Shown(CoverImage image) => IsBlocked && _greyImage is { } grey ? grey : image.Bitmap;

    private async Task LoadCoverAsync(ICoverLoader covers)
    {
        BackdropColors = CoverPalette.ForPlaceholder(Game.Id);
        try
        {
            if (await covers.LoadAsync(Game, _closed.Token) is not { } image)
            {
                return;
            }

            // An icon is small and mostly transparent; its colors still beat the placeholder's. The grey copy is made
            // for every game: the block can come with a later pack list while the page is open.
            var (colors, grey) = await Task.Run(() => (CoverPalette.FromBitmap(image.Bitmap), BitmapPixels.Greyscale(image.Bitmap)), _closed.Token);
            _ui.Post(() =>
            {
                _greyImage = grey;
                Image = image;
                if (colors is { } found)
                {
                    BackdropColors = found;
                    _backdrop = null;
                    OnPropertyChanged(nameof(BackdropColors));
                    OnPropertyChanged(nameof(BackdropBrush));
                    OnPropertyChanged(nameof(BackdropGlowBrush));
                    OnPropertyChanged(nameof(BackdropSecondGlowBrush));
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (IBrush Base, IBrush Glow, IBrush SecondGlow, bool Grey) CreateBackdrop(CoverColors colors, bool grey)
    {
        if (grey)
        {
            colors = new CoverColors(BitmapPixels.Greyscale(colors.Dominant), BitmapPixels.Greyscale(colors.Glow), BitmapPixels.Greyscale(colors.SecondGlow));
        }

        var dominant = colors.Dominant;
        var dark = Color.FromRgb((byte)(dominant.R * 0.45), (byte)(dominant.G * 0.45), (byte)(dominant.B * 0.45));
        return (new ImmutableSolidColorBrush(dark), Glow(colors.Glow, 0.12, 0.6), Glow(colors.SecondGlow, 0.85, 0.5), grey);
    }

    /// <summary>A radial glow from the top edge at <paramref name="x"/> (relative), fading out over <paramref name="radius"/> of the width.</summary>
    private static IBrush Glow(Color color, double x, double radius) => new RadialGradientBrush
    {
        Center = new RelativePoint(x, 0, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(x, 0, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(radius * 2, RelativeUnit.Relative),
        GradientStops = { new GradientStop(color, 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) },
    }.ToImmutable();

    /// <summary>The first 8 hex digits of SHA-256 over the sorted "path hash" lines: a short name for a file-hash version.</summary>
    private static string ShortHash(IReadOnlyDictionary<string, string> files)
    {
        var text = new StringBuilder();
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            text.Append(path).Append(' ').Append(hash).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..8];
    }
}
