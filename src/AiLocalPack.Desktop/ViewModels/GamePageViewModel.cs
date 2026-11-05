using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Toolkit.Engines;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Game page, design §3: a hero header (backdrop from the cover's colors, cover, name, fact chips engine / store /
/// version, the installed pack block, the state's primary action with its caption), the update banner, and the tabs
/// Packs (count), Generation (% while a job runs), Audio and Textures (coming soon). The tab bar sticks to the top with
/// a mini cover, the name and the primary action once the header scrolls away (<see cref="IsTabBarStuck"/>, set by the
/// view). Follows the catalog (rename, drive unplugged), the pack list and the target language while open.
/// </summary>
public sealed partial class GamePageViewModel : PageViewModel
{
    private readonly GameLibrary _library;
    private readonly GameStatuses _statuses;
    private readonly PackIndex _packIndex;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly CancellationTokenSource _closed = new();
    private IBrush? _placeholderBrush;
    private (IBrush Base, IBrush Glow, IBrush SecondGlow)? _backdrop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Cover), nameof(HasCover), nameof(Icon), nameof(HasIcon))]
    private CoverImage? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPacksTab), nameof(IsGenerationTab), nameof(IsSoonTab), nameof(SoonTitle), nameof(SoonText), nameof(SoonIcon))]
    private GamePageTab _selectedTab;

    /// <summary>The header scrolled out of view: the sticky bar shows the mini cover, name and primary action.</summary>
    [ObservableProperty]
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
        GamePageTab initialTab = GamePageTab.Default)
    {
        ArgumentNullException.ThrowIfNull(game);
        Game = game;
        _library = library;
        _statuses = statuses;
        _packIndex = packIndex;
        _settings = settings;
        _localizer = localizer;
        _ui = ui;
        Tabs = [.. new[] { GamePageTab.Packs, GamePageTab.Generation, GamePageTab.Audio, GamePageTab.Textures }
            .Select(t => new GameTabViewModel(t, tab => SelectedTab = tab))];
        PrimaryCommand = new RelayCommand(RunPrimary, () => CanRunPrimary);
        Status = statuses.Resolve(game);
        Action = GamePrimaryActions.Resolve(Status);
        SelectedTab = initialTab != GamePageTab.Default ? initialTab
            : GamePrimaryActions.OpensOnGeneration(Status) ? GamePageTab.Generation
            : GamePageTab.Packs;
        UpdateTabs();

        _library.Catalog.Changed += OnCatalogChanged;
        _packIndex.Changed += OnPackIndexChanged;
        _settings.Changed += OnSettingsChanged;
        _localizer.CultureChanged += OnCultureChanged;
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

    // ---------- Cover and backdrop ----------

    public Bitmap? Cover => Image is { IsIcon: false } image ? image.Bitmap : null;

    public bool HasCover => Cover is not null;

    public Bitmap? Icon => Image is { IsIcon: true } image ? image.Bitmap : null;

    public bool HasIcon => Icon is not null;

    /// <summary>The colors the backdrop is drawn from: the cover's, else the placeholder's (until the cover loads).</summary>
    public CoverColors BackdropColors { get; private set; }

    /// <summary>Made on first read (UI thread), like the library's placeholder brush.</summary>
    public IBrush PlaceholderBrush => _placeholderBrush ??= PlaceholderColors.CreateBrush(Game.Id);

    /// <summary>The backdrop's base: the cover's dominant color, darkened.</summary>
    public IBrush BackdropBrush => Backdrop.Base;

    /// <summary>A soft radial glow at the top left, in the cover's most vivid color.</summary>
    public IBrush BackdropGlowBrush => Backdrop.Glow;

    /// <summary>A second glow at the top right, in another color of the cover.</summary>
    public IBrush BackdropSecondGlowBrush => Backdrop.SecondGlow;

    private (IBrush Base, IBrush Glow, IBrush SecondGlow) Backdrop => _backdrop ??= CreateBackdrop(BackdropColors);

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

    public bool HasPrimaryAction => Action.Kind != GamePrimaryActionKind.None;

    /// <summary>"Install best pack", "Generate translation with AI", "Play", "Update to rev 4", "View progress"…</summary>
    public string PrimaryText => PrimaryActionTexts.Label(Action, _localizer);

    /// <summary>What exactly happens (<see cref="PrimaryActionTexts.Caption"/>).</summary>
    public string PrimaryCaption => PrimaryActionTexts.Caption(Action, Game, _localizer);

    /// <summary>Resource key of the button's icon.</summary>
    public string PrimaryIcon => Action.Kind switch
    {
        GamePrimaryActionKind.InstallBestPack => "IconDownload",
        GamePrimaryActionKind.Play => "IconPlay",
        GamePrimaryActionKind.UpdatePack => "IconRefresh",
        GamePrimaryActionKind.ViewProgress => "IconLoader",
        _ => "IconSparkles",
    };

    /// <summary>View progress is teal (generation), the others the accent.</summary>
    public bool IsPrimaryGen => Action.Kind == GamePrimaryActionKind.ViewProgress;

    /// <summary>Play waits for launching (task 25); the others work.</summary>
    public bool CanRunPrimary => Action.Kind is not (GamePrimaryActionKind.None or GamePrimaryActionKind.Play) && Game.IsAvailable;

    public IRelayCommand PrimaryCommand { get; }

    // ---------- Installed pack block and update banner ----------

    public bool ShowInstalled => Status.Installed is not null && !Status.IsBlocked;

    /// <summary>A game update broke the installed pack: the block is amber with ⚠.</summary>
    public bool IsInstalledBroken => Status.Installed is { BrokenByUpdate: true };

    public string InstalledLabel => _localizer[IsInstalledBroken ? nameof(Strings.Game_Installed_Broken) : nameof(Strings.Game_Installed_Label)];

    /// <summary>"«Фонарщики» · alice · rev 4" (title and author from the registry, the pack ID when it's gone from it).</summary>
    public string InstalledText => Status.Installed is { } installed
        ? InstalledPack is { } pack
            ? _localizer.Format(nameof(Strings.Game_Installed_Text), pack.Title, pack.Author.GitHub, installed.Revision)
            : _localizer.Format(nameof(Strings.Game_Installed_TextNoAuthor), installed.PackId, installed.Revision)
        : string.Empty;

    /// <summary>The amber banner between header and tabs when an update broke the installed pack.</summary>
    public bool ShowUpdateBanner => Status.Kind == LocalizationStatusKind.NeedsUpdate;

    public string UpdateBannerText => Status.Installed is { } installed
        ? _localizer.Format(
            nameof(Strings.Game_UpdateBanner),
            Game.Version?.BuildId is { } build ? _localizer.Format(nameof(Strings.Game_Chip_Build), build) : VersionText,
            InstalledTitle,
            installed.Revision)
        : string.Empty;

    /// <summary>The banner's <c>Translate new strings</c>: only when the header offers the update (else it is the header's action).</summary>
    public bool ShowBannerTranslate => Action.Kind == GamePrimaryActionKind.UpdatePack;

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

    /// <summary>The registry has packs for the game and language.</summary>
    public bool HasPacks => Status.Packs.Count > 0;

    /// <summary>"3 packs into Русский" (Packs tab until its list comes, task 24).</summary>
    public string PacksSummary => _localizer.Plural(nameof(Strings.Game_Packs_Count_One), Status.Packs.Count, LanguageName);

    /// <summary>The empty Packs tab ("No translation into Русский yet"): no packs, the list is known and the game isn't blocked (task 23 explains that).</summary>
    public bool ShowNoPacks => !HasPacks && !IsStatusUnknown && !Status.IsBlocked;

    /// <summary>"No translation into Русский yet".</summary>
    public string NoPacksTitle => _localizer.Format(nameof(Strings.Game_Packs_None), LanguageName);

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
        _closed.Cancel();
    }

    /// <summary>"Generate with AI" on the empty Packs tab, "Translate new strings" on the banner: the Generation tab.</summary>
    [RelayCommand]
    private void OpenGeneration() => SelectedTab = GamePageTab.Generation;

    /// <summary>
    /// Install and Update open the Packs tab until their flows exist (tasks 29, 33); Generate, Translate new strings and
    /// View progress open the Generation tab (its content comes with tasks 52, 53, 56).
    /// </summary>
    private void RunPrimary()
    {
        SelectedTab = Action.Kind switch
        {
            GamePrimaryActionKind.InstallBestPack or GamePrimaryActionKind.UpdatePack => GamePageTab.Packs,
            GamePrimaryActionKind.Generate or GamePrimaryActionKind.TranslateNewStrings or GamePrimaryActionKind.ViewProgress => GamePageTab.Generation,
            _ => SelectedTab,
        };
    }

    partial void OnSelectedTabChanged(GamePageTab value)
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Tab == value;
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

    private void OnCultureChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Recomputes the status and action and re-raises every text (catalog, pack list, language, UI language).</summary>
    private void Refresh()
    {
        Status = _statuses.Resolve(Game);
        Action = GamePrimaryActions.Resolve(Status);
        UpdateTabs();
        OnPropertyChanged(string.Empty);
        PrimaryCommand.NotifyCanExecuteChanged();
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

    private async Task LoadCoverAsync(ICoverLoader covers)
    {
        BackdropColors = CoverPalette.ForPlaceholder(Game.Id);
        try
        {
            if (await covers.LoadAsync(Game, _closed.Token) is not { } image)
            {
                return;
            }

            // An icon is small and mostly transparent; its colors still beat the placeholder's.
            var colors = await Task.Run(() => CoverPalette.FromBitmap(image.Bitmap), _closed.Token);
            _ui.Post(() =>
            {
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

    private static (IBrush Base, IBrush Glow, IBrush SecondGlow) CreateBackdrop(CoverColors colors)
    {
        var dominant = colors.Dominant;
        var dark = Color.FromRgb((byte)(dominant.R * 0.45), (byte)(dominant.G * 0.45), (byte)(dominant.B * 0.45));
        return (new ImmutableSolidColorBrush(dark), Glow(colors.Glow, 0.12, 0.6), Glow(colors.SecondGlow, 0.85, 0.5));
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
