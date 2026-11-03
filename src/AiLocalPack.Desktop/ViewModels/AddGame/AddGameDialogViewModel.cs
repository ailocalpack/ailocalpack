using System.Globalization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.AddGame;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.AddGame;

/// <summary>
/// Add game folder dialog, one game (design §2): step 1 picks a folder (button or drop), the analysis searches it and
/// fingerprints the game, step 2 summarizes it with the primary action for the case: <c>Add game</c>, <c>Open game page</c>
/// (already in the library), <c>Add as unavailable</c> (anti-cheat). A folder without games goes back to step 1 with an
/// error and the hint open. Several games are reviewed one at a time in task 14; until then step 1 asks for one game's
/// folder. The answer block shows a neutral "registry lookup comes later" note until tasks 21 / 29.
/// </summary>
public sealed partial class AddGameDialogViewModel : DialogViewModel
{
    private static readonly GameEngine[] ChoosableEngines =
        [GameEngine.Unity, GameEngine.Unreal, GameEngine.RpgMaker, GameEngine.RenPy, GameEngine.Godot];

    private readonly GameCatalog _catalog;
    private readonly GameFolderAnalyzer _analyzer;
    private readonly AntiCheatPolicy _policy;
    private readonly IFilePicker _picker;
    private readonly ICoverLoader _covers;
    private readonly ManualCoverStore _coverStore;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<AddGameDialogViewModel> _logger;
    private readonly AnalysisStepViewModel _executablesStep;
    private readonly AnalysisStepViewModel _engineStep;
    private readonly AnalysisStepViewModel _fingerprintStep;
    private readonly AnalysisStepViewModel _registryStep;

    private CancellationTokenSource? _work;
    private (byte[] Data, string Extension, bool IsIcon)? _cover;
    private bool _closed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPick), nameof(IsAnalyzing), nameof(IsSummary), nameof(StepText))]
    private AddGameStep _step = AddGameStep.Pick;

    /// <summary>Full path of the folder being analyzed or shown.</summary>
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrimary))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    private GameAnalysis? _analysis;

    /// <summary>The game already in the catalog (same fingerprint or same folder); step 2 then offers its page.</summary>
    [ObservableProperty]
    private Game? _existing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrimary))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    private string _name = string.Empty;

    /// <summary>The name is an input field (✎ pressed).</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>"Edit game details" expanded (opens by itself when the engine is unknown).</summary>
    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private IReadOnlyList<EngineOption> _engineOptions = [];

    [ObservableProperty]
    private EngineOption? _selectedEngine;

    [ObservableProperty]
    private IReadOnlyList<string> _executableOptions = [];

    [ObservableProperty]
    private string? _selectedExecutable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverArt), nameof(CoverIcon), nameof(HasCoverArt), nameof(HasCoverIcon))]
    private CoverImage? _coverImage;

    [ObservableProperty]
    private string? _coverError;

    /// <summary>The game's own icon is shown (or there is none), so "Use game icon" has nothing to undo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseIconCommand))]
    private bool _hasPickedCover;

    /// <summary>Adding (fingerprinting again after another main executable was chosen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrimary))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(BackCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _summaryError;

    public AddGameDialogViewModel(
        GameCatalog catalog,
        GameFolderAnalyzer analyzer,
        AntiCheatPolicy policy,
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
        _policy = policy;
        _picker = picker;
        _covers = covers;
        _coverStore = coverStore;
        _dialogs = dialogs;
        _navigation = navigation;
        _pages = pages;
        _localizer = localizer;
        _ui = ui;
        _logger = logger;
        _executablesStep = new(nameof(Strings.AddGame_Stage_Executables), localizer);
        _engineStep = new(nameof(Strings.AddGame_Stage_Engine), localizer);
        _fingerprintStep = new(nameof(Strings.AddGame_Stage_Fingerprint), localizer);
        _registryStep = new(nameof(Strings.AddGame_Stage_Registry), localizer);
        Steps = [_executablesStep, _engineStep, _fingerprintStep, _registryStep];
        _localizer.CultureChanged += OnCultureChanged;
    }

    public bool IsPick => Step == AddGameStep.Pick;

    public bool IsAnalyzing => Step == AddGameStep.Analyzing;

    public bool IsSummary => Step == AddGameStep.Summary;

    /// <summary>"Step 1 of 2" / "Step 2 of 2" in the header.</summary>
    public string StepText => _localizer.Format(nameof(Strings.AddGame_StepOf), IsPick ? 1 : 2, 2);

    public bool HasPickError => PickError is not null;

    /// <summary>Hint line about desktop shortcuts, for the OS the app runs on.</summary>
    public string HintShortcut => _localizer[
        OperatingSystem.IsWindows() ? nameof(Strings.AddGame_Hint_Shortcut_Windows)
        : OperatingSystem.IsMacOS() ? nameof(Strings.AddGame_Hint_Shortcut_MacOS)
        : nameof(Strings.AddGame_Hint_Shortcut_Linux)];

    /// <summary>Checklist of the analyzing state.</summary>
    public IReadOnlyList<AnalysisStepViewModel> Steps { get; }

    /// <summary>"Analyzing “Wren and Whistle”…"</summary>
    public string AnalyzingTitle => _localizer.Format(nameof(Strings.AddGame_Analyzing_Title), FolderName(FolderPath));

    // ---------- Step 2 ----------

    public string GamePath => Analysis?.Game.Path ?? string.Empty;

    /// <summary>"“Indie” holds one game, in the subfolder GOG/Moss and Mortar."; <c>null</c> when the folder is the game.</summary>
    public string? SubfolderNotice => Analysis is { Game.RelativePath: { Length: > 0 } relative }
        ? _localizer.Format(nameof(Strings.AddGame_Subfolder), FolderName(FolderPath), relative.Replace('/', Path.DirectorySeparatorChar))
        : null;

    public bool HasSubfolderNotice => SubfolderNotice is not null;

    public bool IsEngineUnknown => SelectedEngine is null || SelectedEngine.Kind.Engine == GameEngine.Unknown;

    /// <summary>Engine fact chip.</summary>
    public string EngineText => IsEngineUnknown ? _localizer[nameof(Strings.AddGame_EngineUnknown)] : SelectedEngine!.Display;

    /// <summary>Whether the engine was detected (tag next to the Engine field: "detected" / "not detected").</summary>
    public bool IsEngineDetected => Analysis?.Game.Engine.Kind.Engine is { } engine && engine != GameEngine.Unknown;

    /// <summary>Main executable fact chip: its file name.</summary>
    public string ExecutableText => SelectedExecutable is { } exe ? exe.Split('/')[^1] : _localizer[nameof(Strings.AddGame_NoExecutable)];

    /// <summary>"1,248 files · 812 MB"</summary>
    public string SizeText => Analysis is { } a
        ? $"{_localizer.Plural(nameof(Strings.AddGame_Files_One), a.Size.Files)} · {FormatSize(a.Size.Bytes)}"
        : string.Empty;

    public bool IsDuplicate => Existing is not null;

    public string DuplicateText => Existing is { } game ? _localizer.Format(nameof(Strings.AddGame_Duplicate), game.Name) : string.Empty;

    /// <summary>Anti-cheat of the game as the policy sees it (found in its files, the registry list, the whitelist).</summary>
    private AntiCheatVerdict Verdict => Analysis is { } a ? _policy.Resolve(a.Id, a.AntiCheat.StoredId) : AntiCheatVerdict.None;

    public bool IsBlocked => !IsDuplicate && Verdict.IsBlocked;

    public string AntiCheatTitle => IsBlocked ? _localizer.Format(nameof(Strings.AddGame_AntiCheat_Title), Verdict.Name) : string.Empty;

    /// <summary>Engine-unknown warning (not for a game already in the library).</summary>
    public bool ShowEngineWarning => !IsDuplicate && IsEngineUnknown;

    /// <summary>The neutral answer block until registry lookup exists (tasks 21 / 29).</summary>
    public bool ShowRegistryNote => !IsDuplicate;

    /// <summary>Rename, cover and details are for a game being added, not one already in the library.</summary>
    public bool CanEdit => !IsDuplicate;

    public string PrimaryText => _localizer[
        IsDuplicate ? nameof(Strings.AddGame_OpenGamePage)
        : IsBlocked ? nameof(Strings.AddGame_AddUnavailable)
        : nameof(Strings.AddGame_Add)];

    public bool CanPrimary => Analysis is not null && !IsBusy && (IsDuplicate || !string.IsNullOrWhiteSpace(Name));

    public Bitmap? CoverArt => CoverImage is { IsIcon: false } image ? image.Bitmap : null;

    public Bitmap? CoverIcon => CoverImage is { IsIcon: true } image ? image.Bitmap : null;

    public bool HasCoverArt => CoverArt is not null;

    public bool HasCoverIcon => CoverIcon is not null;

    /// <summary>Esc leaves the name field first; otherwise the dialog closes.</summary>
    public override bool TryHandleEscape()
    {
        if (!IsRenaming)
        {
            return false;
        }

        IsRenaming = false;
        return true;
    }

    public override void OnClosed()
    {
        _closed = true;
        _work?.Cancel();
        _localizer.CultureChanged -= OnCultureChanged;
    }

    /// <summary>Searches <paramref name="folder"/> (picked, dropped on the dialog or on the library) and shows the result.</summary>
    public async Task AnalyzeAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(path))
        {
            ShowPickError(_localizer.Format(nameof(Strings.AddGame_Error_Missing), path), null, openHint: false);
            return;
        }

        FolderPath = path;
        OnPropertyChanged(nameof(AnalyzingTitle));
        foreach (var step in Steps)
        {
            step.Set(AnalysisStepState.Waiting, string.Empty);
        }

        PickError = null;
        PickErrorDetail = null;
        Step = AddGameStep.Analyzing;
        try
        {
            var (scan, analysis) = await Task.Run(() => Search(path, work.Token), work.Token);
            _ui.Post(() => OnSearched(scan, analysis, work));
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
                    ShowPickError(_localizer.Format(nameof(Strings.AddGame_Error_Unreadable), FolderName(path)), e.Message, openHint: false);
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

    /// <summary>Stops the analysis and goes back to step 1.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _work?.Cancel();
        Step = AddGameStep.Pick;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        _work?.Cancel();
        Analysis = null;
        Existing = null;
        Step = AddGameStep.Pick;
    }

    private bool CanGoBack() => !IsBusy;

    [RelayCommand]
    private void Close() => _dialogs.Close(this);

    [RelayCommand]
    private void Rename() => IsRenaming = !IsRenaming;

    [RelayCommand]
    private async Task ChooseCoverAsync()
    {
        if (await _picker.PickImageAsync(_localizer[nameof(Strings.AddGame_PickImage_Title)]) is not { } path)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't read cover image {Path}", path);
            _ui.Post(() => CoverError = _localizer[nameof(Strings.AddGame_Error_Image)]);
            return;
        }

        var image = await _covers.DecodeAsync(bytes, isIcon: false);
        _ui.Post(() =>
        {
            if (image is null)
            {
                CoverError = _localizer[nameof(Strings.AddGame_Error_Image)];
                return;
            }

            CoverError = null;
            CoverImage = image;
            _cover = (bytes, Path.GetExtension(path), false);
            HasPickedCover = true;
        });
    }

    /// <summary>Drops the picked image and shows the game's icon again.</summary>
    [RelayCommand(CanExecute = nameof(HasPickedCover))]
    private async Task UseIconAsync()
    {
        HasPickedCover = false;
        CoverError = null;
        await ShowIconAsync(Analysis);
    }

    /// <summary>Adds the game (opening its page), or opens the page of the game already in the library.</summary>
    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        if (Existing is { } existing)
        {
            OpenPage(existing);
            return;
        }

        if (Analysis is not { } analysis)
        {
            return;
        }

        IsBusy = true;
        SummaryError = null;
        try
        {
            var (id, version) = (analysis.Id, analysis.Version);
            if (SelectedExecutable != analysis.Game.MainExecutable)
            {
                // Another main executable: it is part of the fingerprint and the version.
                var exe = SelectedExecutable;
                (id, version) = await Task.Run(() => _analyzer.Identify(analysis.Game.Path, analysis.Game.Engine.Kind, exe));
            }

            _ui.Post(() => Add(analysis, id, version));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't fingerprint {Folder}", analysis.Game.Path);
            _ui.Post(() =>
            {
                SummaryError = _localizer.Format(nameof(Strings.AddGame_Error_Unreadable), analysis.Game.Name) + " " + e.Message;
                IsBusy = false;
            });
        }
    }

    private void Add(GameAnalysis analysis, GameId id, GameVersion? version)
    {
        IsBusy = false;
        if (_closed)
        {
            return;
        }

        if (_catalog.Find(id) is { } known)
        {
            Existing = known;
            RefreshSummary();
            return;
        }

        var name = Name.Trim();
        var game = new Game(
            id,
            name,
            GameSource.Manual,
            analysis.Game.Path,
            LibraryPath: null,
            version,
            (SelectedEngine?.Kind ?? EngineKind.Unknown).ToString(),
            analysis.AntiCheat.StoredId,
            LastPlayed: null,
            IsAvailable: true);
        if (_cover is { } cover)
        {
            // Before the game enters the catalog, so its library card finds the cover right away.
            _coverStore.Save(id, cover.Data, cover.Extension, cover.IsIcon);
        }

        try
        {
            _catalog.AddManual(game);
        }
        catch (InvalidOperationException)
        {
            // Added meanwhile (the same folder dropped twice).
            Existing = _catalog.Find(id);
            RefreshSummary();
            return;
        }

        _logger.LogInformation("Added game {Id} ({Name}) from {Folder}", id, name, game.InstallPath);
        OpenPage(game);
    }

    private void OpenPage(Game game)
    {
        _dialogs.Close(this);
        _navigation.NavigateTo(_pages.Create<GamePageViewModel>(game));
    }

    /// <summary>Runs off the UI thread: the folder scan, then the analysis of its only game, updating the checklist.</summary>
    private (FolderScan Scan, GameAnalysis? Analysis) Search(string path, CancellationToken cancellationToken)
    {
        Report(_executablesStep, AnalysisStepState.Running);
        var scan = _analyzer.Scan(path, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (scan.Games.Count != 1)
        {
            return (scan, null);
        }

        var game = scan.Games[0];
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
        return (scan, analysis);
    }

    private void Report(AnalysisStepViewModel step, AnalysisStepState state, string? detail = null) =>
        _ui.Post(() => step.Set(state, detail));

    private void OnSearched(FolderScan scan, GameAnalysis? analysis, CancellationTokenSource work)
    {
        if (_work != work || work.IsCancellationRequested)
        {
            return;
        }

        var folder = FolderName(scan.Folder);
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
            case > 1:
                ShowPickError(
                    _localizer.Plural(nameof(Strings.AddGame_Error_Several_One), scan.Games.Count, folder),
                    _localizer[nameof(Strings.AddGame_Error_Several_Detail)],
                    openHint: false);
                break;
            default:
                ShowSummary(analysis!);
                break;
        }
    }

    private void ShowPickError(string error, string? detail, bool openHint)
    {
        PickError = error;
        PickErrorDetail = detail;
        IsHintOpen = openHint || IsHintOpen;
        Analysis = null;
        Existing = null;
        Step = AddGameStep.Pick;
    }

    private void ShowSummary(GameAnalysis analysis)
    {
        Analysis = analysis;
        Existing = _catalog.Find(analysis.Id) ?? _catalog.Games.FirstOrDefault(g => SamePath(g.InstallPath, analysis.Game.Path));
        Name = analysis.Game.Name;
        IsRenaming = false;
        SummaryError = null;
        CoverError = null;
        var detected = analysis.Game.Engine.Kind;
        EngineOptions = BuildEngineOptions(detected);
        SelectedEngine = EngineOptions.FirstOrDefault(o => o.Kind == detected) ?? EngineOptions[^1];
        ExecutableOptions = analysis.Executables;
        SelectedExecutable = analysis.Game.MainExecutable ?? analysis.Executables.FirstOrDefault();
        IsDetailsOpen = detected.Engine == GameEngine.Unknown;
        HasPickedCover = false;
        CoverImage = null;
        _cover = null;
        RefreshSummary();
        Step = AddGameStep.Summary;
        _ = ShowIconAsync(analysis);
    }

    private async Task ShowIconAsync(GameAnalysis? analysis)
    {
        _cover = null;
        CoverImage = null;
        if (analysis?.Icon is not { } icon)
        {
            return;
        }

        var image = await _covers.DecodeAsync(icon.Data, isIcon: true);
        _ui.Post(() =>
        {
            if (Analysis == analysis && !HasPickedCover)
            {
                CoverImage = image;
                _cover = (icon.Data, icon.Extension, true);
            }
        });
    }

    private List<EngineOption> BuildEngineOptions(EngineKind detected)
    {
        var options = new List<EngineOption>();
        if (detected.Engine != GameEngine.Unknown)
        {
            options.Add(new EngineOption(detected, detected.DisplayName));
        }

        options.AddRange(ChoosableEngines
            .Where(e => e != detected.Engine)
            .Select(e => new EngineOption(new EngineKind(e), new EngineKind(e).DisplayName)));
        options.Add(new EngineOption(EngineKind.Unknown, _localizer[nameof(Strings.AddGame_Engine_DontKnow)]));
        return options;
    }

    partial void OnSelectedEngineChanged(EngineOption? value) => RefreshSummary();

    partial void OnSelectedExecutableChanged(string? value) => OnPropertyChanged(nameof(ExecutableText));

    partial void OnExistingChanged(Game? value) => RefreshSummary();

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(GamePath));
        OnPropertyChanged(nameof(SubfolderNotice));
        OnPropertyChanged(nameof(HasSubfolderNotice));
        OnPropertyChanged(nameof(IsEngineUnknown));
        OnPropertyChanged(nameof(IsEngineDetected));
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(ExecutableText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(IsDuplicate));
        OnPropertyChanged(nameof(DuplicateText));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(AntiCheatTitle));
        OnPropertyChanged(nameof(ShowEngineWarning));
        OnPropertyChanged(nameof(ShowRegistryNote));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(CanPrimary));
        PrimaryCommand.NotifyCanExecuteChanged();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var step in Steps)
        {
            step.RefreshTexts();
        }

        if (Analysis is { } analysis)
        {
            var selected = SelectedEngine?.Kind;
            EngineOptions = BuildEngineOptions(analysis.Game.Engine.Kind);
            SelectedEngine = EngineOptions.FirstOrDefault(o => o.Kind == selected) ?? EngineOptions[^1];
        }

        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(HintShortcut));
        OnPropertyChanged(nameof(AnalyzingTitle));
        RefreshSummary();
    }

    /// <summary>"812 MB", "1.9 GB" in the UI culture.</summary>
    public string FormatSize(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < (long)Kb => _localizer.Format(nameof(Strings.AddGame_Size_Bytes), bytes),
            < (long)Mb => _localizer.Format(nameof(Strings.AddGame_Size_KB), Math.Round(bytes / Kb).ToString("N0", _localizer.Culture)),
            < (long)Gb => _localizer.Format(nameof(Strings.AddGame_Size_MB), Math.Round(bytes / Mb).ToString("N0", _localizer.Culture)),
            _ => _localizer.Format(nameof(Strings.AddGame_Size_GB), (bytes / Gb).ToString("N1", _localizer.Culture)),
        };
    }

    private static string FolderName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

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
