using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.AddGame;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Toolkit.Engines;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.AddGame;

/// <summary>Services a <see cref="GameSummaryViewModel"/> needs; one set per add-folder dialog.</summary>
public sealed record GameSummaryServices(
    GameCatalog Catalog,
    GameFolderAnalyzer Analyzer,
    AntiCheatPolicy Policy,
    IFilePicker Picker,
    ICoverLoader Covers,
    ManualCoverStore CoverStore,
    Localizer Localizer,
    IUiDispatcher Ui,
    ILogger Logger);

/// <summary>
/// One analyzed game in the add-folder dialog (design §2, step 2): cover, name (✎), fact chips, notices (already in the
/// library, anti-cheat, engine unknown, registry note) and Edit game details. Used for the one-game summary and for each
/// game of a folder with several games, so edits survive going back and forth between games.
/// </summary>
public sealed partial class GameSummaryViewModel : ObservableObject
{
    private static readonly GameEngine[] ChoosableEngines =
        [GameEngine.Unity, GameEngine.Unreal, GameEngine.RpgMaker, GameEngine.RenPy, GameEngine.Godot];

    private readonly GameSummaryServices _s;
    private readonly string? _scannedFolder;
    private (byte[] Data, string Extension, bool IsIcon)? _cover;
    private bool _cancelled;

    /// <summary>The game already in the catalog (same fingerprint or same folder), or the one just added from here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private Game? _existing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _name;

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
    private string? _selectedExecutable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverArt), nameof(CoverIcon), nameof(HasCoverArt), nameof(HasCoverIcon))]
    private CoverImage? _coverImage;

    [ObservableProperty]
    private string? _coverError;

    /// <summary>A picked image is shown, so "Use game icon" has something to undo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseIconCommand))]
    private bool _hasPickedCover;

    /// <summary>Adding (fingerprinting again after another main executable was chosen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <param name="analysis">The analyzed game.</param>
    /// <param name="existing">The game already in the library, or <c>null</c>.</param>
    /// <param name="scannedFolder">
    /// The folder the player picked, for the "found in a subfolder" notice of a folder with one game; <c>null</c> when the
    /// game is one of several (the review shows its path instead).
    /// </param>
    public GameSummaryViewModel(GameAnalysis analysis, Game? existing, string? scannedFolder, GameSummaryServices services)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(services);
        _s = services;
        _scannedFolder = scannedFolder;
        Analysis = analysis;
        _existing = existing;
        _name = analysis.Game.Name;
        var detected = analysis.Game.Engine.Kind;
        _engineOptions = BuildEngineOptions(detected);
        _selectedEngine = _engineOptions.FirstOrDefault(o => o.Kind == detected) ?? _engineOptions[^1];
        ExecutableOptions = analysis.Executables;
        _selectedExecutable = analysis.Game.MainExecutable ?? analysis.Executables.FirstOrDefault();
        _isDetailsOpen = detected.Engine == GameEngine.Unknown;
    }

    public GameAnalysis Analysis { get; }

    public IReadOnlyList<string> ExecutableOptions { get; }

    public string GamePath => Analysis.Game.Path;

    /// <summary>"“Indie” holds one game, in the subfolder GOG/Moss and Mortar."; <c>null</c> when the folder is the game.</summary>
    public string? SubfolderNotice => _scannedFolder is not null && Analysis.Game.RelativePath is { Length: > 0 } relative
        ? _s.Localizer.Format(nameof(Strings.AddGame_Subfolder), AddGameText.FolderName(_scannedFolder), AddGameText.NativePath(relative))
        : null;

    public bool HasSubfolderNotice => SubfolderNotice is not null;

    public bool IsEngineUnknown => SelectedEngine is null || SelectedEngine.Kind.Engine == GameEngine.Unknown;

    /// <summary>Engine fact chip.</summary>
    public string EngineText => IsEngineUnknown ? _s.Localizer[nameof(Strings.AddGame_EngineUnknown)] : SelectedEngine!.Display;

    /// <summary>Whether the engine was detected (tag next to the Engine field: "detected" / "not detected").</summary>
    public bool IsEngineDetected => Analysis.Game.Engine.Kind.Engine != GameEngine.Unknown;

    /// <summary>Main executable fact chip: its file name.</summary>
    public string ExecutableText => SelectedExecutable is { } exe ? exe.Split('/')[^1] : _s.Localizer[nameof(Strings.AddGame_NoExecutable)];

    /// <summary>"1,248 files · 812 MB"</summary>
    public string SizeText =>
        $"{_s.Localizer.Plural(nameof(Strings.AddGame_Files_One), Analysis.Size.Files)} · {AddGameText.FormatSize(_s.Localizer, Analysis.Size.Bytes)}";

    public bool IsDuplicate => Existing is not null;

    /// <summary>The game was added from this summary (the review shows "Added" when going back to it).</summary>
    public bool WasAdded { get; private set; }

    public string DuplicateText => Existing is { } game
        ? _s.Localizer.Format(WasAdded ? nameof(Strings.AddGame_Added) : nameof(Strings.AddGame_Duplicate), game.Name)
        : string.Empty;

    /// <summary>Anti-cheat of the game as the policy sees it (found in its files, the registry list, the whitelist).</summary>
    public AntiCheatVerdict Verdict => _s.Policy.Resolve(Analysis.Id, Analysis.AntiCheat.StoredId);

    public bool IsBlocked => !IsDuplicate && Verdict.IsBlocked;

    public string AntiCheatTitle => IsBlocked ? _s.Localizer.Format(nameof(Strings.AddGame_AntiCheat_Title), Verdict.Name) : string.Empty;

    /// <summary>Engine-unknown warning (not for a game already in the library).</summary>
    public bool ShowEngineWarning => !IsDuplicate && IsEngineUnknown;

    /// <summary>The neutral answer block until registry lookup exists (tasks 21 / 29).</summary>
    public bool ShowRegistryNote => !IsDuplicate;

    /// <summary>Rename, cover and details are for a game being added, not one already in the library.</summary>
    public bool CanEdit => !IsDuplicate;

    /// <summary>Whether "Add" can run now (not one already in the library: that one offers its page or the next game).</summary>
    public bool CanAdd => !IsDuplicate && !IsBusy && !string.IsNullOrWhiteSpace(Name);

    public Bitmap? CoverArt => CoverImage is { IsIcon: false } image ? image.Bitmap : null;

    public Bitmap? CoverIcon => CoverImage is { IsIcon: true } image ? image.Bitmap : null;

    public bool HasCoverArt => CoverArt is not null;

    public bool HasCoverIcon => CoverIcon is not null;

    /// <summary>Esc leaves the name field; <c>false</c> when it wasn't open.</summary>
    public bool TryHandleEscape()
    {
        if (!IsRenaming)
        {
            return false;
        }

        IsRenaming = false;
        return true;
    }

    /// <summary>The dialog closed: an add still fingerprinting won't enter the catalog.</summary>
    public void Cancel() => _cancelled = true;

    /// <summary>Shows the game's icon on the cover (decoded off the UI thread).</summary>
    public Task LoadIconAsync() => ShowIconAsync();

    /// <summary>
    /// Adds the game to the catalog: fingerprinted again when another main executable was chosen, the cover saved first.
    /// Returns the added game, or <c>null</c> when it is already in the library (then <see cref="Existing"/> is set), the
    /// files can't be read (<see cref="Error"/>) or the dialog closed meanwhile.
    /// </summary>
    public async Task<Game?> AddAsync()
    {
        if (!CanAdd)
        {
            return null;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var (id, version) = (Analysis.Id, Analysis.Version);
            if (SelectedExecutable != Analysis.Game.MainExecutable)
            {
                // Another main executable: it is part of the fingerprint and the version.
                var exe = SelectedExecutable;
                (id, version) = await Task.Run(() => _s.Analyzer.Identify(Analysis.Game.Path, Analysis.Game.Engine.Kind, exe));
            }

            return await OnUiAsync(() => Add(id, version));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _s.Logger.LogWarning(e, "Can't fingerprint {Folder}", Analysis.Game.Path);
            await OnUiAsync(() =>
            {
                Error = _s.Localizer.Format(nameof(Strings.AddGame_Error_Unreadable), Analysis.Game.Name) + " " + e.Message;
                return (Game?)null;
            });
            return null;
        }
        finally
        {
            _s.Ui.Post(() => IsBusy = false);
        }
    }

    private Game? Add(GameId id, GameVersion? version)
    {
        if (_cancelled)
        {
            return null;
        }

        if (_s.Catalog.Find(id) is { } known)
        {
            Existing = known;
            return null;
        }

        var name = Name.Trim();
        var game = new Game(
            id,
            name,
            GameSource.Manual,
            Analysis.Game.Path,
            LibraryPath: null,
            version,
            (SelectedEngine?.Kind ?? EngineKind.Unknown).ToString(),
            Analysis.AntiCheat.StoredId,
            LastPlayed: null,
            IsAvailable: true,
            SelectedExecutable);
        if (_cover is { } cover)
        {
            // Before the game enters the catalog, so its library card finds the cover right away.
            _s.CoverStore.Save(id, cover.Data, cover.Extension, cover.IsIcon);
        }

        try
        {
            _s.Catalog.AddManual(game);
        }
        catch (InvalidOperationException)
        {
            // Added meanwhile (the same folder dropped twice).
            Existing = _s.Catalog.Find(id);
            return null;
        }

        _s.Logger.LogInformation("Added game {Id} ({Name}) from {Folder}", id, name, game.InstallPath);
        IsRenaming = false;
        WasAdded = true;
        Existing = game;
        return game;
    }

    [RelayCommand]
    private void Rename() => IsRenaming = !IsRenaming;

    [RelayCommand]
    private async Task ChooseCoverAsync()
    {
        if (await _s.Picker.PickImageAsync(_s.Localizer[nameof(Strings.AddGame_PickImage_Title)]) is not { } path)
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
            _s.Logger.LogWarning(e, "Can't read cover image {Path}", path);
            _s.Ui.Post(() => CoverError = _s.Localizer[nameof(Strings.AddGame_Error_Image)]);
            return;
        }

        var image = await _s.Covers.DecodeAsync(bytes, isIcon: false);
        _s.Ui.Post(() =>
        {
            if (image is null)
            {
                CoverError = _s.Localizer[nameof(Strings.AddGame_Error_Image)];
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
        await ShowIconAsync();
    }

    private async Task ShowIconAsync()
    {
        _cover = null;
        CoverImage = null;
        if (Analysis.Icon is not { } icon)
        {
            return;
        }

        var image = await _s.Covers.DecodeAsync(icon.Data, isIcon: true);
        _s.Ui.Post(() =>
        {
            if (!HasPickedCover)
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
        options.Add(new EngineOption(EngineKind.Unknown, _s.Localizer[nameof(Strings.AddGame_Engine_DontKnow)]));
        return options;
    }

    partial void OnSelectedEngineChanged(EngineOption? value) => Refresh();

    partial void OnSelectedExecutableChanged(string? value) => OnPropertyChanged(nameof(ExecutableText));

    partial void OnExistingChanged(Game? value) => Refresh();

    /// <summary>The UI language changed: texts and the engine list again.</summary>
    public void RefreshTexts()
    {
        var selected = SelectedEngine?.Kind;
        EngineOptions = BuildEngineOptions(Analysis.Game.Engine.Kind);
        SelectedEngine = EngineOptions.FirstOrDefault(o => o.Kind == selected) ?? EngineOptions[^1];
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(SubfolderNotice));
        OnPropertyChanged(nameof(HasSubfolderNotice));
        OnPropertyChanged(nameof(IsEngineUnknown));
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
        OnPropertyChanged(nameof(CanAdd));
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and returns its result.</summary>
    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _s.Ui.Post(() =>
        {
            try
            {
                done.SetResult(action());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }
}

/// <summary>Formatting shared by the add-folder dialog's screens.</summary>
internal static class AddGameText
{
    public static string FolderName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    /// <summary>A '/'-separated relative path with the OS separator.</summary>
    public static string NativePath(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>"812 MB", "1.9 GB" in the UI culture.</summary>
    public static string FormatSize(Localizer localizer, long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < (long)Kb => localizer.Format(nameof(Strings.AddGame_Size_Bytes), bytes),
            < (long)Mb => localizer.Format(nameof(Strings.AddGame_Size_KB), Math.Round(bytes / Kb).ToString("N0", localizer.Culture)),
            < (long)Gb => localizer.Format(nameof(Strings.AddGame_Size_MB), Math.Round(bytes / Mb).ToString("N0", localizer.Culture)),
            _ => localizer.Format(nameof(Strings.AddGame_Size_GB), (bytes / Gb).ToString("N1", localizer.Culture)),
        };
    }
}
