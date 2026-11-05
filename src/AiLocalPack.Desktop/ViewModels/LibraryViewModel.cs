using System.Globalization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.ViewModels.Library;
using AiLocalPack.Desktop.ViewModels.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Library (home) screen, design §1: the catalog's games as a cover grid, each with its localization status for the
/// "Translate to" language (the <see cref="AppSettings.GameLanguage"/> setting), with search, status filter chips
/// (live counts) and sort (<c>Important first</c> by default). States: scanning (banner; skeleton cards while no game
/// is known yet), a damaged saved list (banner), registry unreachable (banner with the saved list's date and
/// <c>Retry</c>; badges faded), Steam not found / empty, nothing found for the search and filter.
/// </summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    /// <summary>Skeleton cards shown while the first scan runs with an empty catalog.</summary>
    public const int SkeletonCount = 12;

    private readonly GameLibrary _library;
    private readonly ICoverLoader _covers;
    private readonly AntiCheatPolicy _policy;
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ISystemShell _shell;
    private readonly IDialogService _dialogs;
    private readonly ManualCoverStore _manualCovers;
    private readonly PackIndex _packIndex;
    private readonly SettingsStore _settings;
    private readonly Dictionary<GameId, GameCardViewModel> _cards = [];
    private bool _syncingLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoResultsText))]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoResultsText))]
    private LibraryFilter _selectedFilter;

    [ObservableProperty]
    private ChoiceOption _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRegistryBanner), nameof(RegistryBannerText), nameof(IsRegistryStale))]
    private RegistryIndexSnapshot? _registry;

    [ObservableProperty]
    private bool _isRetrying;

    [ObservableProperty]
    private LibrarySortOption _selectedSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(HasNoResults))]
    private IReadOnlyList<GameCardViewModel> _visibleCards = [];

    [ObservableProperty]
    private IReadOnlyList<LibrarySortOption> _sortOptions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCatalogDamaged))]
    private bool _catalogDamagedDismissed;

    public LibraryViewModel(
        GameLibrary library,
        ICoverLoader covers,
        AntiCheatPolicy policy,
        INavigationService navigation,
        IPageFactory pages,
        Localizer localizer,
        IUiDispatcher ui,
        ISystemShell shell,
        IDialogService dialogs,
        ManualCoverStore manualCovers,
        PackIndex packIndex,
        SettingsStore settings)
    {
        _library = library;
        _covers = covers;
        _policy = policy;
        _navigation = navigation;
        _pages = pages;
        _localizer = localizer;
        _ui = ui;
        _shell = shell;
        _dialogs = dialogs;
        _manualCovers = manualCovers;
        _packIndex = packIndex;
        _settings = settings;
        _sortOptions = BuildSortOptions();
        _selectedSort = _sortOptions[0];
        LanguageOptions = Settings.LanguageOptions.ForGames(localizer, UiLanguages.Os);
        _selectedLanguage = FindLanguageOption(settings.Current.GameLanguage);
        FilterChips = [.. LibraryFilters.All.Select(f => new LibraryFilterChip(f, filter => SelectedFilter = filter))];
        _registry = packIndex.Current;
        _localizer.CultureChanged += (_, _) => OnCultureChanged();
        _library.Catalog.Changed += (_, _) => _ui.Post(SyncCards);
        _library.StateChanged += (_, _) => _ui.Post(OnScanStateChanged);
        _packIndex.Changed += (_, snapshot) => _ui.Post(() => OnPackIndexChanged(snapshot));
        _settings.Changed += (_, s) => _ui.Post(() => OnSettingsChanged(s));
        UpdateChipLabels();
        SyncCards();
    }

    public override string Title => Strings.Library_Title;

    /// <summary>All games of the catalog (search and sort applied in <see cref="VisibleCards"/>).</summary>
    public IReadOnlyCollection<GameCardViewModel> Cards => _cards.Values;

    /// <summary>Placeholders for the skeleton grid.</summary>
    public IReadOnlyList<int> Skeletons { get; } = [.. Enumerable.Range(0, SkeletonCount)];

    /// <summary>The first scan hasn't finished, or a refresh is running.</summary>
    public bool IsScanning => !_library.HasScanned || _library.IsScanning;

    public bool HasGames => _cards.Count > 0;

    /// <summary>The "Translate to" picker: "System (&lt;OS language&gt;)" and every game language; edits the setting.</summary>
    public IReadOnlyList<ChoiceOption> LanguageOptions { get; }

    /// <summary>The language badges and counts are for: an exact <see cref="GameLanguages.Tags"/> tag.</summary>
    public string Language => GameLanguages.Resolve(_settings.Current.GameLanguage, UiLanguages.Os);

    /// <summary>"Translate to" label before the picker.</summary>
    public string LanguageLabel => _localizer[nameof(Strings.Library_Language_Label)];

    /// <summary>The status filter chips (All, Installed, Pack available, No pack, Needs update, Generating).</summary>
    public IReadOnlyList<LibraryFilterChip> FilterChips { get; }

    /// <summary>Chips are hidden while there are no games (first scan, Steam not found).</summary>
    public bool ShowFilters => HasGames;

    /// <summary>The pack list is the saved copy (registry unreachable): badges are faded.</summary>
    public bool IsRegistryStale => Registry is { IsStale: true };

    /// <summary>The registry couldn't be read this time: a banner with <c>Retry</c>.</summary>
    public bool ShowRegistryBanner => Registry is { IsFresh: false };

    /// <summary>
    /// "Can't reach the pack registry. Showing data saved on &lt;date&gt;." (saved copy), "… Pack statuses appear once
    /// it answers." (no saved copy), or "… newer format; update the app".
    /// </summary>
    public string RegistryBannerText => Registry switch
    {
        null or { IsFresh: true } => string.Empty,
        { Problem: RegistryIndexProblem.NewerFormat } => _localizer[nameof(Strings.Library_Registry_NewerFormat)],
        { Problem: RegistryIndexProblem.InvalidLocation } => _localizer[nameof(Strings.Library_Registry_InvalidAddress)],
        { IsStale: true, FetchedAt: { } at } => _localizer.Format(
            nameof(Strings.Library_Registry_Stale),
            at.ToLocalTime().ToString("g", _localizer.Culture)),
        _ => _localizer[nameof(Strings.Library_Registry_Unavailable)],
    };

    /// <summary>Skeleton cards instead of the grid: scanning and nothing known yet (first launch).</summary>
    public bool ShowSkeletons => IsScanning && !HasGames;

    public bool ShowGrid => VisibleCards.Count > 0;

    /// <summary>Scan finished with no games at all: "Steam library not found" with ways to add games.</summary>
    public bool IsEmpty => !IsScanning && !HasGames;

    /// <summary>Games exist but the search hides them all.</summary>
    public bool HasNoResults => HasGames && VisibleCards.Count == 0;

    /// <summary>
    /// The saved game list was damaged at startup and rebuilt from the scan (design §9): a banner says manual games
    /// need adding again and where the old file is, until dismissed. Shown once the scan has found the Steam games.
    /// </summary>
    public bool ShowCatalogDamaged =>
        _library.Catalog.LoadOutcome == GameCatalogLoadOutcome.Corrupted && _library.HasScanned && !CatalogDamagedDismissed;

    public string CatalogDamagedText => _library.Catalog.CorruptedBackupPath is { } backup
        ? _localizer.Format(nameof(Strings.Library_CatalogDamaged), Path.GetFileName(backup))
        : _localizer[nameof(Strings.Library_CatalogDamaged_NoBackup)];

    /// <summary>Why nothing shows: the search, the filter, or both.</summary>
    public string NoResultsText => (Query.Trim(), SelectedFilter) switch
    {
        ({ Length: 0 }, _) => _localizer[nameof(Strings.Library_NoResults_Filter)],
        (var q, LibraryFilter.All) => _localizer.Format(nameof(Strings.Library_NoResults_Text), q),
        (var q, _) => _localizer.Format(nameof(Strings.Library_NoResults_SearchAndFilter), q),
    };

    /// <summary>"18 games" (screen reader name of the grid).</summary>
    public string GameCount => _localizer.Plural(nameof(Strings.Library_GameCount_One), _cards.Count);

    /// <summary>Shortcut hint in the search box.</summary>
    public static string SearchShortcut => OperatingSystem.IsMacOS() ? "⌘ F" : "Ctrl F";

    /// <summary>Esc: clears the search when there is one; otherwise the window goes back.</summary>
    public override bool TryHandleEscape()
    {
        if (Query.Length == 0)
        {
            return false;
        }

        Query = string.Empty;
        return true;
    }

    /// <summary>"Reset search and filter" in the nothing-found state.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        Query = string.Empty;
        SelectedFilter = LibraryFilter.All;
    }

    /// <summary>Fetches the pack list again (offline banner).</summary>
    [RelayCommand]
    private async Task RetryRegistryAsync()
    {
        IsRetrying = true;
        try
        {
            await _packIndex.RefreshAsync();
        }
        finally
        {
            IsRetrying = false;
        }
    }

    [RelayCommand]
    private void DismissCatalogDamaged() => CatalogDamagedDismissed = true;

    /// <summary>Opens the add-folder dialog on step 1 (design §2).</summary>
    [RelayCommand]
    private void AddFolder() => _dialogs.Show<AddGameDialogViewModel>();

    /// <summary>A folder dropped on the library: the dialog opens straight on the analysis (step 1 skipped).</summary>
    public Task AddDroppedFolderAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return _dialogs.Show<AddGameDialogViewModel>().AnalyzeAsync(folder);
    }

    [RelayCommand]
    private void OpenLibrarySettings() => _navigation.NavigateTo(_pages.Create<SettingsViewModel>(SettingsSectionId.Libraries));

    partial void OnQueryChanged(string value) => UpdateVisible();

    partial void OnSelectedSortChanged(LibrarySortOption value) => UpdateVisible();

    partial void OnSelectedFilterChanged(LibraryFilter value) => UpdateVisible();

    partial void OnSelectedLanguageChanged(ChoiceOption value)
    {
        if (_syncingLanguage || value is null)
        {
            return;
        }

        var tag = (string?)value.Value;
        if (_settings.Current.GameLanguage != tag)
        {
            _settings.Update(s => s with { GameLanguage = tag });
        }

        ApplyStatuses();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var option = FindLanguageOption(settings.GameLanguage);
        if (!ReferenceEquals(option, SelectedLanguage))
        {
            // Changed elsewhere (Settings → General) or the save failed and the stored value is back.
            _syncingLanguage = true;
            SelectedLanguage = option;
            _syncingLanguage = false;
        }

        ApplyStatuses();
    }

    private void OnPackIndexChanged(RegistryIndexSnapshot snapshot)
    {
        Registry = snapshot;
        ApplyStatuses();
    }

    private ChoiceOption FindLanguageOption(string? tag) =>
        LanguageOptions.FirstOrDefault(o => string.Equals((string?)o.Value, tag, StringComparison.OrdinalIgnoreCase)) ?? LanguageOptions[0];

    private void SyncCards()
    {
        // Steam games of a library that isn't available (unplugged drive, SD card) are hidden until it's back
        // (Settings → Libraries says so); a manual game whose folder is gone stays, dimmed, so it can be removed.
        var games = _library.Catalog.Games.Where(g => g.Source == GameSource.Manual || g.IsAvailable).ToList();
        var ids = games.Select(g => g.Id).ToHashSet();
        foreach (var gone in _cards.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _cards.Remove(gone);
        }

        foreach (var game in games)
        {
            if (_cards.TryGetValue(game.Id, out var card))
            {
                card.Update(game);
            }
            else
            {
                card = new GameCardViewModel(game, _localizer, OpenGame, OpenFolder, Remove, QuickAction);
                _cards.Add(game.Id, card);
                _ = card.LoadCoverAsync(_covers);
            }
        }

        OnPropertyChanged(nameof(Cards));
        OnPropertyChanged(nameof(GameCount));
        OnScanStateChanged();
        ApplyStatuses();
    }

    /// <summary>Recomputes every card's status for the current language and pack list, then the counts and the grid.</summary>
    private void ApplyStatuses()
    {
        var language = Language;
        var index = Registry?.Index;
        var stale = IsRegistryStale;
        foreach (var card in _cards.Values)
        {
            // Installed packs (task 29), broken ones (task 33) and generation jobs (task 47) join here.
            card.ApplyStatus(LocalizationStatuses.Resolve(card.Game, language, _policy.Resolve(card.Game), index), stale);
        }

        foreach (var chip in FilterChips)
        {
            chip.Count = _cards.Values.Count(c => LibraryFilters.Matches(chip.Filter, c.StatusKind));
            chip.CountText = chip.Count.ToString("N0", _localizer.Culture);
        }

        OnPropertyChanged(nameof(Language));
        UpdateVisible();
    }

    /// <summary>
    /// The card's quick action. Until the flows exist (Install: task 29, Update: 33, View / Resume / Generate: the
    /// Generation tab, tasks 22 and 52) each opens the game page.
    /// </summary>
    private void QuickAction(GameCardViewModel card) => OpenGame(card);

    private void OpenGame(GameCardViewModel card) => _navigation.NavigateTo(_pages.Create<GamePageViewModel>(card.Game));

    private void OpenFolder(GameCardViewModel card) => _shell.OpenFolder(card.Game.InstallPath);

    /// <summary>Removes a manual game and its stored cover; its folder stays on disk.</summary>
    private void Remove(GameCardViewModel card)
    {
        if (card.Game.Source == GameSource.Manual && _library.Catalog.RemoveManual(card.Game.Id))
        {
            _manualCovers.Delete(card.Game.Id);
        }
    }

    private void OnScanStateChanged()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(ShowSkeletons));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(ShowCatalogDamaged));
        OnPropertyChanged(nameof(ShowFilters));
    }

    private void UpdateVisible()
    {
        foreach (var chip in FilterChips)
        {
            chip.IsSelected = chip.Filter == SelectedFilter;
        }

        var matching = _cards.Values.Where(c =>
            LibraryFilters.Matches(SelectedFilter, c.StatusKind)
            && (string.IsNullOrWhiteSpace(Query) || TextSearch.Matches(c.Name, Query)));
        VisibleCards = [.. Sort(matching, SelectedSort.Value, _localizer.Culture)];
    }

    /// <summary>Cards in the given order; ties (and never played games) by name, then ID so the order is stable.</summary>
    public static IEnumerable<GameCardViewModel> Sort(IEnumerable<GameCardViewModel> cards, LibrarySort sort, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var byName = StringComparer.Create(culture, CompareOptions.IgnoreCase);
        var ordered = sort switch
        {
            LibrarySort.ImportantFirst => cards
                .OrderBy(c => LocalizationStatuses.Importance(c.StatusKind))
                .ThenBy(c => c.Game.LastPlayed is null)
                .ThenByDescending(c => c.Game.LastPlayed)
                .ThenBy(c => c.Name, byName),
            LibrarySort.RecentlyPlayed => cards
                .OrderBy(c => c.Game.LastPlayed is null)
                .ThenByDescending(c => c.Game.LastPlayed)
                .ThenBy(c => c.Name, byName),
            _ => cards.OrderBy(c => c.Name, byName),
        };
        return ordered.ThenBy(c => c.Game.Id.ToString(), StringComparer.Ordinal);
    }

    private void OnCultureChanged()
    {
        var selected = SelectedSort.Value;
        SortOptions = BuildSortOptions();
        SelectedSort = SortOptions.First(o => o.Value == selected);
        foreach (var card in _cards.Values)
        {
            card.RefreshTexts();
        }

        foreach (var option in LanguageOptions)
        {
            option.Refresh();
        }

        UpdateChipLabels();
        ApplyStatuses();
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(RegistryBannerText));
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(CatalogDamagedText));
        UpdateVisible();
    }

    private void UpdateChipLabels()
    {
        foreach (var chip in FilterChips)
        {
            chip.Label = _localizer[chip.Filter switch
            {
                LibraryFilter.Installed => nameof(Strings.Library_Status_Installed),
                LibraryFilter.PackAvailable => nameof(Strings.Library_Status_PackAvailable),
                LibraryFilter.NoPack => nameof(Strings.Library_Status_NoPack),
                LibraryFilter.NeedsUpdate => nameof(Strings.Library_Status_NeedsUpdate),
                LibraryFilter.Generating => nameof(Strings.Library_Filter_Generating),
                _ => nameof(Strings.Library_Filter_All),
            }];
        }
    }

    private List<LibrarySortOption> BuildSortOptions() =>
    [
        new(LibrarySort.ImportantFirst, _localizer[nameof(Strings.Library_Sort_ImportantFirst)]),
        new(LibrarySort.RecentlyPlayed, _localizer[nameof(Strings.Library_Sort_RecentlyPlayed)]),
        new(LibrarySort.ByName, _localizer[nameof(Strings.Library_Sort_ByName)]),
    ];
}
