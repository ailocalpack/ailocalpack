using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.AddGame;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.AddGame;

/// <summary>
/// Add game folder dialog (design §2). Step 1 picks a folder (button or drop); the search then streams the games it finds.
/// One game: the analyzing checklist, then its summary with the primary action for the case (<c>Add game</c>,
/// <c>Open game page</c>, <c>Add as unavailable</c>). Several games: the scanning state (<c>Stop and show</c>), an
/// overview (tally, cover strip, skipped folders), a one-at-a-time review (Previous / Skip / Add; games already in the
/// library or unreadable are skipped automatically) and a result. A folder without games goes back to step 1 with an
/// error and the hint open. The answer block shows a neutral "registry lookup comes later" note until tasks 21 / 29.
/// </summary>
public sealed partial class AddGameDialogViewModel : DialogViewModel
{
    /// <summary>Least time between two scan progress updates on the UI thread (a game found is always shown at once).</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(80);

    private readonly GameCatalog _catalog;
    private readonly GameFolderAnalyzer _analyzer;
    private readonly IFilePicker _picker;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<AddGameDialogViewModel> _logger;
    private readonly GameSummaryServices _summaryServices;
    private readonly AnalysisStepViewModel _executablesStep;
    private readonly AnalysisStepViewModel _engineStep;
    private readonly AnalysisStepViewModel _fingerprintStep;
    private readonly AnalysisStepViewModel _registryStep;

    private CancellationTokenSource? _work;
    private CancellationTokenSource? _stop;
    private FolderScan? _scan;
    private bool _closed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsPick), nameof(IsAnalyzing), nameof(IsSummary), nameof(IsScanning), nameof(IsOverview), nameof(IsReview),
        nameof(IsResult), nameof(IsSeveral), nameof(ShowSummary), nameof(Title), nameof(StepText), nameof(PrimaryText))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private AddGameStep _step = AddGameStep.Pick;

    /// <summary>Full path of the folder being searched or shown.</summary>
    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPickError))]
    private string? _pickError;

    [ObservableProperty]
    private string? _pickErrorDetail;

    /// <summary>"Where is my game folder?" expanded (opens by itself with a "no games" error).</summary>
    [ObservableProperty]
    private bool _isHintOpen;

    /// <summary>The game shown: the folder's only game, or the one under review.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(CanPrimary), nameof(IsPrimaryQuiet))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(BackCommand))]
    private GameSummaryViewModel? _summary;

    // ---------- Scanning ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanProgressText))]
    private int _foldersScanned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanProgressText))]
    private int _foldersFound;

    /// <summary>The search is over and the games found are being analyzed (fingerprint, anti-cheat).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isCheckingGames;

    /// <summary>"Checking game 2 of 5: Moss and Mortar…"</summary>
    [ObservableProperty]
    private string _checkingText = string.Empty;

    // ---------- Several games ----------

    [ObservableProperty]
    private IReadOnlyList<FolderGameViewModel> _games = [];

    [ObservableProperty]
    private IReadOnlyList<FolderGameViewModel> _queue = [];

    [ObservableProperty]
    private IReadOnlyList<TallyItem> _tally = [];

    [ObservableProperty]
    private IReadOnlyList<SkippedFolderViewModel> _skippedFolders = [];

    /// <summary>Index in <see cref="Queue"/> of the game under review.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private int _reviewIndex;

    public AddGameDialogViewModel(
        GameCatalog catalog,
        GameFolderAnalyzer analyzer,
        AntiCheatRules antiCheatRules,
        IFilePicker picker,
        ICoverLoader covers,
        ManualCoverStore coverStore,
        IDialogService dialogs,
        INavigationService navigation,
        IPageFactory pages,
        Localizer localizer,
        IUiDispatcher ui,
        ILogger<AddGameDialogViewModel> logger)
    {
        _catalog = catalog;
        _analyzer = analyzer;
        _picker = picker;
        _dialogs = dialogs;
        _navigation = navigation;
        _pages = pages;
        _localizer = localizer;
        _ui = ui;
        _logger = logger;
        _summaryServices = new GameSummaryServices(catalog, analyzer, antiCheatRules.Current, picker, covers, coverStore, localizer, ui, logger);
        _executablesStep = new(nameof(Strings.AddGame_Stage_Executables), localizer);
        _engineStep = new(nameof(Strings.AddGame_Stage_Engine), localizer);
        _fingerprintStep = new(nameof(Strings.AddGame_Stage_Fingerprint), localizer);
        _registryStep = new(nameof(Strings.AddGame_Stage_Registry), localizer);
        Steps = [_executablesStep, _engineStep, _fingerprintStep, _registryStep];
        _localizer.CultureChanged += OnCultureChanged;
    }

    public bool IsPick => Step == AddGameStep.Pick;

    public bool IsAnalyzing => Step == AddGameStep.Analyzing;

    /// <summary>One game's summary (the folder holds one game).</summary>
    public bool IsSummary => Step == AddGameStep.Summary;

    public bool IsScanning => Step == AddGameStep.Scanning;

    public bool IsOverview => Step == AddGameStep.Overview;

    public bool IsReview => Step == AddGameStep.Review;

    public bool IsResult => Step == AddGameStep.Result;

    /// <summary>A folder with several games ("Add games").</summary>
    public bool IsSeveral => Step is AddGameStep.Scanning or AddGameStep.Overview or AddGameStep.Review or AddGameStep.Result;

    /// <summary>The game summary is on screen (one game, or the game under review).</summary>
    public bool ShowSummary => Step is AddGameStep.Summary or AddGameStep.Review;

    /// <summary>"Add game" / "Add games".</summary>
    public string Title => _localizer[IsSeveral ? nameof(Strings.AddGame_TitleSeveral) : nameof(Strings.AddGame_Title)];

    /// <summary>Header text on the right: "Step 1 of 2", "Finding games", "Step 2 of 3", "Game 2 of 5", "Step 3 of 3".</summary>
    public string StepText => Step switch
    {
        AddGameStep.Pick => _localizer.Format(nameof(Strings.AddGame_StepOf), 1, 2),
        AddGameStep.Analyzing or AddGameStep.Summary => _localizer.Format(nameof(Strings.AddGame_StepOf), 2, 2),
        AddGameStep.Scanning => _localizer[nameof(Strings.AddGame_Scanning_Step)],
        AddGameStep.Overview => _localizer.Format(nameof(Strings.AddGame_StepOf), 2, 3),
        AddGameStep.Review => _localizer.Format(nameof(Strings.AddGame_Review_GameOf), ReviewIndex + 1, Queue.Count),
        _ => _localizer.Format(nameof(Strings.AddGame_StepOf), 3, 3),
    };

    public bool HasPickError => PickError is not null;

    /// <summary>Hint line about desktop shortcuts, for the OS the app runs on.</summary>
    public string HintShortcut => _localizer[
        OperatingSystem.IsWindows() ? nameof(Strings.AddGame_Hint_Shortcut_Windows)
        : OperatingSystem.IsMacOS() ? nameof(Strings.AddGame_Hint_Shortcut_MacOS)
        : nameof(Strings.AddGame_Hint_Shortcut_Linux)];

    /// <summary>Checklist of the analyzing state.</summary>
    public IReadOnlyList<AnalysisStepViewModel> Steps { get; }

    /// <summary>"Analyzing “Wren and Whistle”…"</summary>
    public string AnalyzingTitle => _localizer.Format(nameof(Strings.AddGame_Analyzing_Title), AddGameText.FolderName(FolderPath));

    // ---------- Primary action (one game and review) ----------

    public string PrimaryText => Summary switch
    {
        null => string.Empty,
        { IsDuplicate: true } => _localizer[IsReview ? nameof(Strings.AddGame_Review_Next) : nameof(Strings.AddGame_OpenGamePage)],
        { IsBlocked: true } => _localizer[nameof(Strings.AddGame_AddUnavailable)],
        _ => _localizer[IsReview ? nameof(Strings.AddGame_Review_Add) : nameof(Strings.AddGame_Add)],
    };

    public bool CanPrimary => Summary is { IsBusy: false } summary && (summary.IsDuplicate || summary.CanAdd);

    /// <summary>The primary button is drawn quiet for "Add as unavailable".</summary>
    public bool IsPrimaryQuiet => Summary is { IsBlocked: true };

    // ---------- Scanning ----------

    /// <summary>Games streaming in while the folder is searched.</summary>
    public ObservableCollection<ScannedGameViewModel> FoundGames { get; } = [];

    /// <summary>"Looking for games in “Indie”…"</summary>
    public string ScanningTitle => _localizer.Format(nameof(Strings.AddGame_Scanning_Title), AddGameText.FolderName(FolderPath));

    /// <summary>"Scanned 14 of 37 folders"</summary>
    public string ScanProgressText => _localizer.Format(nameof(Strings.AddGame_Scanning_Progress), FoldersScanned, FoldersFound);

    /// <summary>"Found 3 games · you can stop and review what's found"</summary>
    public string ScanFoundText => _localizer.Plural(nameof(Strings.AddGame_Scanning_Found_One), FoundGames.Count);

    // ---------- Overview ----------

    /// <summary>"Found 5 games in “Indie”"</summary>
    public string OverviewTitle => _localizer.Plural(nameof(Strings.AddGame_Overview_Title_One), Games.Count, AddGameText.FolderName(FolderPath));

    /// <summary>The search was stopped by the player: the overview shows what was found until then.</summary>
    public bool WasStopped => _scan is { Stopped: true };

    /// <summary>The search stopped at its size limit: there may be more games.</summary>
    public bool WasLimitReached => _scan is { LimitReached: true };

    /// <summary>A Steam library was picked: its games are already in the library through Steam.</summary>
    public bool IsSteamLibrary => _scan is { IsSteamLibrary: true };

    public bool HasSkippedFolders => SkippedFolders.Count > 0;

    /// <summary>"Skipped 3 folders without games"</summary>
    public string SkippedTitle => _localizer.Plural(nameof(Strings.AddGame_Skipped_Title_One), SkippedFolders.Count);

    /// <summary>"5 games, one at a time" in the overview footer, or "Nothing new to add".</summary>
    public string QueueText => Queue.Count > 0
        ? _localizer.Plural(nameof(Strings.AddGame_Overview_Queue_One), Queue.Count)
        : _localizer[nameof(Strings.AddGame_Overview_Nothing)];

    public bool CanStart => Queue.Count > 0;

    // ---------- Result ----------

    /// <summary>"Added 4 games" or "No games added".</summary>
    public string ResultTitle => Games.Count(g => g.IsAdded) is var added and > 0
        ? _localizer.Plural(nameof(Strings.AddGame_Result_Title_One), added)
        : _localizer[nameof(Strings.AddGame_Result_None)];

    /// <summary>Esc leaves the name field first; otherwise the dialog closes.</summary>
    public override bool TryHandleEscape() => Summary?.TryHandleEscape() ?? false;

    public override void OnClosed()
    {
        _closed = true;
        _work?.Cancel();
        Summary?.Cancel();
        foreach (var game in Games)
        {
            game.Summary?.Cancel();
        }

        _localizer.CultureChanged -= OnCultureChanged;
    }

    /// <summary>Searches <paramref name="folder"/> (picked, dropped on the dialog or on the library) and shows the result.</summary>
    public async Task AnalyzeAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        var stop = _stop = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(path))
        {
            ShowPickError(_localizer.Format(nameof(Strings.AddGame_Error_Missing), path), null, openHint: false);
            return;
        }

        FolderPath = path;
        OnPropertyChanged(nameof(AnalyzingTitle));
        OnPropertyChanged(nameof(ScanningTitle));
        foreach (var step in Steps)
        {
            step.Set(AnalysisStepState.Waiting, string.Empty);
        }

        FoundGames.Clear();
        OnPropertyChanged(nameof(ScanFoundText));
        FoldersScanned = 0;
        FoldersFound = 0;
        IsCheckingGames = false;
        PickError = null;
        PickErrorDetail = null;
        Summary = null;
        Step = AddGameStep.Analyzing;
        try
        {
            var result = await Task.Run(() => Search(path, work, stop.Token), work.Token);
            _ui.Post(() => OnSearched(result, work));
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            // Cancelled or replaced by another folder: whoever cancelled has moved on.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't analyze {Folder}", path);
            _ui.Post(() =>
            {
                if (_work == work)
                {
                    ShowPickError(_localizer.Format(nameof(Strings.AddGame_Error_Unreadable), AddGameText.FolderName(path)), e.Message, openHint: false);
                }
            });
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await _picker.PickFolderAsync(_localizer[nameof(Strings.AddGame_PickFolder_Title)]) is { } folder)
        {
            await AnalyzeAsync(folder);
        }
    }

    /// <summary>Stops the search or the analysis and goes back to step 1.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _work?.Cancel();
        Step = AddGameStep.Pick;
    }

    /// <summary>"Stop and show": ends the search and reviews the games found so far.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _stop?.Cancel();

    private bool CanStop() => IsScanning && !IsCheckingGames;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        _work?.Cancel();
        Summary = null;
        Games = [];
        Queue = [];
        Step = AddGameStep.Pick;
    }

    private bool CanGoBack() => Summary is not { IsBusy: true };

    [RelayCommand]
    private void Close() => _dialogs.Close(this);

    /// <summary>
    /// One game: adds it and opens its page, or opens the page of the game already in the library. Review: adds the game
    /// and moves on to the next one (a game already in the library just moves on).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        if (Summary is not { } summary)
        {
            return;
        }

        if (IsReview)
        {
            var item = Queue[ReviewIndex];
            if (summary.IsDuplicate || await summary.AddAsync() is not null)
            {
                _ui.Post(() =>
                {
                    if (summary.WasAdded)
                    {
                        item.State = ReviewState.Added;
                    }

                    if (Summary == summary)
                    {
                        Next();
                    }
                });
            }

            return;
        }

        if (summary.Existing is { } existing)
        {
            OpenPage(existing);
        }
        else if (await summary.AddAsync() is { } added)
        {
            _ui.Post(() => OpenPage(added));
        }
    }

    // ---------- Several games: navigation ----------

    /// <summary>
    /// "Start adding": from the first game not reviewed yet. Games are reviewed in order only; the overview's
    /// covers aren't clickable, so no game is jumped to and none is left unseen.
    /// </summary>
    [RelayCommand]
    private void Start()
    {
        var index = Queue.ToList().FindIndex(g => !g.IsDone);
        if (Queue.Count > 0)
        {
            ShowReview(Math.Max(index, 0));
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => ShowReview(ReviewIndex - 1);

    private bool CanGoPrevious() => ReviewIndex > 0;

    [RelayCommand]
    private void Skip()
    {
        if (!IsReview)
        {
            return;
        }

        var item = Queue[ReviewIndex];
        if (item.State == ReviewState.Pending)
        {
            item.State = ReviewState.Skipped;
        }

        Next();
    }

    /// <summary>"‹ All games": back to the overview (choices made so far stay).</summary>
    [RelayCommand]
    private void AllGames()
    {
        foreach (var game in Queue)
        {
            game.IsCurrent = false;
        }

        Summary = null;
        Step = AddGameStep.Overview;
    }

    /// <summary>Result: closes the dialog on the library, which shows the new games.</summary>
    [RelayCommand]
    private void ToLibrary()
    {
        _dialogs.Close(this);
        while (_navigation.CanGoBack)
        {
            _navigation.GoBack();
        }
    }

    private void Next()
    {
        if (ReviewIndex + 1 < Queue.Count)
        {
            ShowReview(ReviewIndex + 1);
        }
        else
        {
            ShowResult();
        }
    }

    private void ShowReview(int index)
    {
        for (var i = 0; i < Queue.Count; i++)
        {
            Queue[i].IsCurrent = i == index;
        }

        ReviewIndex = index;
        Summary = Queue[index].Summary;
        Step = AddGameStep.Review;
        OnPropertyChanged(nameof(PrimaryText));
    }

    private void ShowResult()
    {
        foreach (var game in Queue)
        {
            game.IsCurrent = false;
        }

        Summary = null;
        OnPropertyChanged(nameof(ResultTitle));
        Step = AddGameStep.Result;
    }

    private void OpenPage(Game game)
    {
        _dialogs.Close(this);
        _navigation.NavigateTo(_pages.Create<GamePageViewModel>(game));
    }

    // ---------- Search (off the UI thread) ----------

    /// <summary>What the search found: the scan, and the analysis of its only game or of each of its games.</summary>
    private sealed record SearchResult(FolderScan Scan, GameAnalysis? Single, IReadOnlyList<(FoundGame Game, GameAnalysis? Analysis, string? Error)> Several);

    /// <summary>Runs off the UI thread: the folder scan, then the analysis of its game(s), reporting progress.</summary>
    private SearchResult Search(string path, CancellationTokenSource work, CancellationToken stop)
    {
        Report(_executablesStep, AnalysisStepState.Running);
        var clock = Stopwatch.StartNew();
        var scan = _analyzer.Scan(
            path,
            new InlineProgress<FolderScanProgress>(p =>
            {
                if (p.Game is null && clock.Elapsed < ProgressInterval)
                {
                    return;
                }

                clock.Restart();
                _ui.Post(() => OnScanProgress(p, work));
            }),
            stop);
        work.Token.ThrowIfCancellationRequested();
        return scan.Games.Count switch
        {
            0 => new SearchResult(scan, null, []),
            1 => new SearchResult(scan, AnalyzeOne(scan.Games[0], work.Token), []),
            _ => new SearchResult(scan, null, AnalyzeAll(scan.Games, work)),
        };
    }

    private void OnScanProgress(FolderScanProgress progress, CancellationTokenSource work)
    {
        if (_work != work)
        {
            return;
        }

        FoldersScanned = progress.FoldersScanned;
        FoldersFound = progress.FoldersFound;
        if (progress.Game is { } game)
        {
            FoundGames.Add(new ScannedGameViewModel(
                game.Name,
                game.Engine.Kind.Engine == GameEngine.Unknown ? _localizer[nameof(Strings.AddGame_EngineUnknown)] : game.Engine.Kind.DisplayName,
                game.RelativePath.Length > 0 ? AddGameText.NativePath(game.RelativePath) : "."));
            OnPropertyChanged(nameof(ScanFoundText));
            if (FoundGames.Count > 1 && Step == AddGameStep.Analyzing)
            {
                Step = AddGameStep.Scanning;
            }
        }
    }

    /// <summary>The folder's only game, updating the checklist.</summary>
    private GameAnalysis AnalyzeOne(FoundGame game, CancellationToken cancellationToken)
    {
        Report(_executablesStep, AnalysisStepState.Done, game.MainExecutable?.Split('/')[^1] ?? string.Empty);
        Report(_engineStep, AnalysisStepState.Running);
        var engine = game.Engine.Kind.Engine == GameEngine.Unknown ? _localizer[nameof(Strings.AddGame_EngineUnknown)] : game.Engine.Kind.DisplayName;
        var fingerprinting = false;
        var analysis = _analyzer.Analyze(
            game,
            new InlineProgress<AnalysisProgress>(p =>
            {
                if (p.Stage != AnalysisStage.Fingerprint)
                {
                    return;
                }

                if (!fingerprinting)
                {
                    fingerprinting = true;
                    Report(_engineStep, AnalysisStepState.Done, engine);
                }

                Report(_fingerprintStep, AnalysisStepState.Running, _localizer.Format(nameof(Strings.AddGame_Stage_FilesOf), p.FilesDone, p.FilesTotal));
            }),
            cancellationToken);
        Report(_engineStep, AnalysisStepState.Done, engine);
        Report(_fingerprintStep, AnalysisStepState.Done);
        Report(_registryStep, AnalysisStepState.Done, _localizer[nameof(Strings.AddGame_Stage_Later)]);
        return analysis;
    }

    /// <summary>Each game of a folder with several; a game whose files can't be read keeps the error instead.</summary>
    private List<(FoundGame, GameAnalysis?, string?)> AnalyzeAll(IReadOnlyList<FoundGame> games, CancellationTokenSource work)
    {
        _ui.Post(() =>
        {
            // Posted from this thread, so it may run after the overview is shown (the result comes back inline).
            if (_work == work && Step is AddGameStep.Analyzing or AddGameStep.Scanning)
            {
                IsCheckingGames = true;
                Step = AddGameStep.Scanning;
            }
        });
        var results = new List<(FoundGame, GameAnalysis?, string?)>(games.Count);
        for (var i = 0; i < games.Count; i++)
        {
            var (game, number) = (games[i], i + 1);
            _ui.Post(() => CheckingText = _localizer.Format(nameof(Strings.AddGame_Scanning_Checking), number, games.Count, game.Name));
            try
            {
                results.Add((game, _analyzer.Analyze(game, cancellationToken: work.Token), null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "Can't analyze {Folder}", game.Path);
                results.Add((game, null, e.Message));
            }
        }

        return results;
    }

    private void Report(AnalysisStepViewModel step, AnalysisStepState state, string? detail = null) =>
        _ui.Post(() => step.Set(state, detail));

    private void OnSearched(SearchResult result, CancellationTokenSource work)
    {
        if (_work != work || work.IsCancellationRequested || _closed)
        {
            return;
        }

        var scan = result.Scan;
        var folder = AddGameText.FolderName(scan.Folder);
        switch (scan.Games.Count)
        {
            case 0 when scan.LimitReached:
                ShowPickError(_localizer.Format(nameof(Strings.AddGame_Error_TooLarge), folder), null, openHint: false);
                break;
            case 0:
                ShowPickError(
                    _localizer.Format(nameof(Strings.AddGame_Error_NoGames), folder),
                    _localizer[nameof(Strings.AddGame_Error_NoGames_Detail)],
                    openHint: true);
                break;
            case 1:
                ShowOne(result.Single!, scan.Folder);
                break;
            default:
                ShowOverview(scan, result.Several);
                break;
        }
    }

    private void ShowPickError(string error, string? detail, bool openHint)
    {
        PickError = error;
        PickErrorDetail = detail;
        IsHintOpen = openHint || IsHintOpen;
        Summary = null;
        Step = AddGameStep.Pick;
    }

    private void ShowOne(GameAnalysis analysis, string folder)
    {
        var summary = new GameSummaryViewModel(analysis, FindExisting(analysis), folder, _summaryServices);
        Summary = summary;
        Step = AddGameStep.Summary;
        _ = summary.LoadIconAsync();
    }

    private void ShowOverview(FolderScan scan, IReadOnlyList<(FoundGame Game, GameAnalysis? Analysis, string? Error)> analyzed)
    {
        _scan = scan;
        var games = new List<FolderGameViewModel>(analyzed.Count);
        var number = 0;
        foreach (var (game, analysis, error) in analyzed)
        {
            var summary = analysis is null ? null : new GameSummaryViewModel(analysis, FindExisting(analysis), null, _summaryServices);
            var kind = summary switch
            {
                null => FolderGameKind.Unreadable,
                { IsDuplicate: true } => FolderGameKind.InLibrary,
                { IsBlocked: true } => FolderGameKind.AntiCheat,
                _ => FolderGameKind.New,
            };
            var item = new FolderGameViewModel(game, summary, error, _localizer) { Kind = kind };
            if (item.IsReviewable)
            {
                item.Number = ++number;
            }

            games.Add(item);
            _ = summary?.LoadIconAsync();
        }

        Games = games;
        Queue = [.. games.Where(g => g.IsReviewable)];
        RefreshOverview();
        IsCheckingGames = false;
        Step = AddGameStep.Overview;
    }

    private void RefreshOverview()
    {
        Tally = [.. Games
            .GroupBy(g => g.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new TallyItem(g.Key, _localizer.Plural(TallyKey(g.Key), g.Count())))];
        SkippedFolders = _scan is null
            ? []
            : [.. _scan.Skipped.Select(s => new SkippedFolderViewModel(AddGameText.NativePath(s.RelativePath), _localizer[ReasonKey(s.Reason)]))];
        OnPropertyChanged(nameof(OverviewTitle));
        OnPropertyChanged(nameof(WasStopped));
        OnPropertyChanged(nameof(WasLimitReached));
        OnPropertyChanged(nameof(IsSteamLibrary));
        OnPropertyChanged(nameof(HasSkippedFolders));
        OnPropertyChanged(nameof(SkippedTitle));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private static string TallyKey(FolderGameKind kind) => kind switch
    {
        FolderGameKind.AntiCheat => nameof(Strings.AddGame_Tally_AntiCheat_One),
        FolderGameKind.InLibrary => nameof(Strings.AddGame_Tally_InLibrary_One),
        FolderGameKind.Unreadable => nameof(Strings.AddGame_Tally_Unreadable_One),
        _ => nameof(Strings.AddGame_Tally_New_One),
    };

    private static string ReasonKey(SkipReason reason) => reason switch
    {
        SkipReason.Redistributable => nameof(Strings.AddGame_Skip_Redistributable),
        SkipReason.Installer => nameof(Strings.AddGame_Skip_Installer),
        SkipReason.Utility => nameof(Strings.AddGame_Skip_Utility),
        SkipReason.Saves => nameof(Strings.AddGame_Skip_Saves),
        _ => nameof(Strings.AddGame_Skip_NoGame),
    };

    /// <summary>The library game with the same fingerprint, or in the same folder (any source).</summary>
    private Game? FindExisting(GameAnalysis analysis) =>
        _catalog.Find(analysis.Id) ?? _catalog.Games.FirstOrDefault(g => SamePath(g.InstallPath, analysis.Game.Path));

    partial void OnSummaryChanged(GameSummaryViewModel? oldValue, GameSummaryViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSummaryPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSummaryPropertyChanged;
        }
    }

    private void OnSummaryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameSummaryViewModel.IsBusy) or nameof(GameSummaryViewModel.CanAdd)
            or nameof(GameSummaryViewModel.IsDuplicate) or nameof(GameSummaryViewModel.IsBlocked))
        {
            OnPropertyChanged(nameof(PrimaryText));
            OnPropertyChanged(nameof(CanPrimary));
            OnPropertyChanged(nameof(IsPrimaryQuiet));
            PrimaryCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var step in Steps)
        {
            step.RefreshTexts();
        }

        Summary?.RefreshTexts();
        foreach (var game in Games)
        {
            if (game.Summary != Summary)
            {
                game.Summary?.RefreshTexts();
            }

            game.RefreshTexts();
        }

        RefreshOverview();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(HintShortcut));
        OnPropertyChanged(nameof(AnalyzingTitle));
        OnPropertyChanged(nameof(ScanningTitle));
        OnPropertyChanged(nameof(ScanProgressText));
        OnPropertyChanged(nameof(ScanFoundText));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(ResultTitle));
    }

    /// <summary>"812 MB", "1.9 GB" in the UI culture.</summary>
    public string FormatSize(long bytes) => AddGameText.FormatSize(_localizer, bytes);

    /// <summary>Same folder; case-insensitive on Windows and macOS, whose usual file systems ignore case.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>Reports on the calling thread.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>One chip of the overview tally: "3 new", "1 with anti-cheat", "1 already in the library".</summary>
public sealed record TallyItem(FolderGameKind Kind, string Text)
{
    public bool IsBad => Kind is FolderGameKind.AntiCheat or FolderGameKind.Unreadable;

    public bool IsInLibrary => Kind == FolderGameKind.InLibrary;
}
