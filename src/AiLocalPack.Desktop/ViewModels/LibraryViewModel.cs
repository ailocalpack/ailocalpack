using System.Globalization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.ViewModels.Library;
using AiLocalPack.Desktop.ViewModels.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Library (home) screen, design §1: the catalog's games as a cover grid with search and sort. States: scanning (banner;
/// skeleton cards while no game is known yet), a damaged saved list (banner), Steam not found / empty, nothing found
/// for the search. Registry statuses, filter chips and the language selector come in task 21.
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
    private readonly Dictionary<GameId, GameCardViewModel> _cards = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoResultsText))]
    private string _query = string.Empty;

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
        ManualCoverStore manualCovers)
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
        _sortOptions = BuildSortOptions();
        _selectedSort = _sortOptions[0];
        _localizer.CultureChanged += (_, _) => OnCultureChanged();
        _library.Catalog.Changed += (_, _) => _ui.Post(SyncCards);
        _library.StateChanged += (_, _) => _ui.Post(OnScanStateChanged);
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

    public string NoResultsText => _localizer.Format(nameof(Strings.Library_NoResults_Text), Query.Trim());

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

    [RelayCommand]
    private void ClearSearch() => Query = string.Empty;

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
                card = new GameCardViewModel(game, _localizer, _policy, OpenGame, OpenFolder, Remove);
                _cards.Add(game.Id, card);
                _ = card.LoadCoverAsync(_covers);
            }
        }

        OnPropertyChanged(nameof(Cards));
        OnPropertyChanged(nameof(GameCount));
        OnScanStateChanged();
        UpdateVisible();
    }

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
    }

    private void UpdateVisible()
    {
        var matching = _cards.Values.Where(c => string.IsNullOrWhiteSpace(Query) || TextSearch.Matches(c.Name, Query));
        VisibleCards = [.. Sort(matching, SelectedSort.Value, _localizer.Culture)];
    }

    /// <summary>Cards in the given order; ties (and never played games) by name, then ID so the order is stable.</summary>
    public static IEnumerable<GameCardViewModel> Sort(IEnumerable<GameCardViewModel> cards, LibrarySort sort, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var byName = StringComparer.Create(culture, CompareOptions.IgnoreCase);
        var ordered = sort switch
        {
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

        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(CatalogDamagedText));
        UpdateVisible();
    }

    private List<LibrarySortOption> BuildSortOptions() =>
    [
        new(LibrarySort.ByName, _localizer[nameof(Strings.Library_Sort_ByName)]),
        new(LibrarySort.RecentlyPlayed, _localizer[nameof(Strings.Library_Sort_RecentlyPlayed)]),
    ];
}
