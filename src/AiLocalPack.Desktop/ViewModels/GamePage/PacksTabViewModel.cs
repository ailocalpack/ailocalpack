using System.Collections.ObjectModel;
using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>Order of the Packs tab's list.</summary>
public enum PackSort
{
    /// <summary><see cref="LocalizationStatuses.Rank"/>: made for your build, verified, likes, reports, newest; the default.</summary>
    StatusRating,

    /// <summary>Last updated first.</summary>
    Newest,

    /// <summary>Most strings translated first.</summary>
    Coverage,
}

/// <summary>An entry of the sort dropdown.</summary>
public sealed record PackSortOption(PackSort Value, string Display);

/// <summary>A language of the language popover: its native name, the game's pack count in it ("—" when none).</summary>
public sealed class PackLanguageOption(string tag, string name, int count, bool isCurrent, Action<string> select)
{
    public string Tag { get; } = tag;

    public string Name { get; } = name;

    public int Count { get; } = count;

    public string CountText => Count > 0 ? Count.ToString("N0", CultureInfo.CurrentCulture) : "—";

    public bool IsCurrent { get; } = isCurrent;

    /// <summary>"English (2)" in the empty state's "Available in other languages".</summary>
    public string NameWithCount => $"{Name} ({Count.ToString("N0", CultureInfo.CurrentCulture)})";

    public IRelayCommand SelectCommand { get; } = new RelayCommand(() => select(tag));

    internal string EnglishName { get; init; } = string.Empty;
}

/// <summary>
/// The game page's Packs tab (design §4; task 24): registry packs of the game in one language with a details panel.
/// Toolbar: the language popover (search, every language with this game's pack count), the sort (status + rating /
/// newest / coverage) and the pack count. The list starts with the page's target language and follows it while the
/// player hasn't picked another one. Manifests are fetched when the tab is shown, the selected pack first, then the
/// rest of the list one at a time, and each is checked against the player's files (<see cref="PackManifests"/>), so
/// the rows fill in their compatibility. States: pack list unknown, no packs in the language (with the other
/// languages that have some), the saved list while offline (banner with <c>Retry</c>, list faded, install off).
/// The Mine section (task 55) on top: the game's generation job (<c>creating 42%</c>, opens the Generation tab) and the
/// packs generated on this machine in any language (<see cref="MinePackRowViewModel"/>: install, uninstall, delete;
/// publishing comes with task 61). <c>Install</c> raises <see cref="InstallRequested"/> and the installed pack's
/// <c>Uninstall</c> <see cref="UninstallRequested"/> (the page runs the install flow); while an operation runs the rows
/// show it (<see cref="SetInstallState"/>).
/// </summary>
public sealed partial class PacksTabViewModel : ObservableObject
{
    private readonly PackIndex _packIndex;
    private readonly PackManifests _manifests;
    private readonly Localizer _localizer;
    private readonly ISystemShell _shell;
    private readonly Action _openGeneration;
    private readonly LocalPacks _localPacks;
    private readonly GenerationService _jobs;
    private readonly Action<LocalPack> _installLocal;
    private readonly Action<LocalPack> _deleteLocal;
    private readonly Func<LocalPack, PackPublishViewModel> _publish;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closed = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PackRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private List<PackLanguageOption> _languages = [];
    private Game _game;
    private LocalizationStatus _status;
    private string _target;
    private bool _active;
    private PackSort _sort = PackSort.StatusRating;
    private bool _pumping;
    private bool _busy;
    private string? _installingPackId;
    private Task _pump = Task.CompletedTask;

    [ObservableProperty]
    private PackRowViewModel? _selected;

    /// <summary>The selected Mine row (its details replace the registry pack's), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMineDetails), nameof(ShowPackDetails))]
    private MinePackRowViewModel? _selectedMine;

    [ObservableProperty]
    private PackSortOption _selectedSort;

    [ObservableProperty]
    private string _languageQuery = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryRegistryCommand))]
    private bool _isRetrying;

    public PacksTabViewModel(
        Game game,
        LocalizationStatus status,
        string language,
        PackIndex packIndex,
        PackManifests manifests,
        Localizer localizer,
        ISystemShell shell,
        Action openGeneration,
        LocalPacks localPacks,
        GenerationService jobs,
        Action<LocalPack> installLocal,
        Action<LocalPack> deleteLocal,
        Func<LocalPack, PackPublishViewModel> publish,
        ILogger logger)
    {
        _publish = publish;
        _game = game;
        _status = status;
        _target = language;
        Language = language;
        _packIndex = packIndex;
        _manifests = manifests;
        _localizer = localizer;
        _shell = shell;
        _openGeneration = openGeneration;
        _localPacks = localPacks;
        _jobs = jobs;
        _installLocal = installLocal;
        _deleteLocal = deleteLocal;
        _logger = logger;
        SortOptions = BuildSortOptions();
        _selectedSort = SortOptions[0];
        Rebuild();
    }

    /// <summary><c>Install</c> on a pack, with its manifest and check (the install flow listens).</summary>
    public event EventHandler<PackRowViewModel>? InstallRequested;

    /// <summary><c>Uninstall (restore originals)</c> on the installed pack.</summary>
    public event EventHandler? UninstallRequested;

    /// <summary>The language the list shows (exact tag).</summary>
    public string Language { get; private set; }

    /// <summary>"Русский".</summary>
    public string LanguageName => LanguageNames.Native(Language);

    public ObservableCollection<PackRowViewModel> Rows { get; } = [];

    /// <summary>The Mine section: the game's open job, then its local packs (any language).</summary>
    public ObservableCollection<MinePackRowViewModel> Mine { get; } = [];

    public bool HasMine => Mine.Count > 0;

    /// <summary>The details panel shows the selected Mine row.</summary>
    public bool ShowMineDetails => SelectedMine is not null;

    /// <summary>The details panel shows the selected registry pack.</summary>
    public bool ShowPackDetails => SelectedMine is null && Selected is not null;

    /// <summary>"No packs into Русский in the registry yet." under the Registry heading when only Mine has rows.</summary>
    public string RegistryNoneText => _localizer.Format(nameof(Strings.Packs_Registry_None), LanguageName);

    public IReadOnlyList<PackSortOption> SortOptions { get; private set; }

    /// <summary>The popover's languages matching <see cref="LanguageQuery"/>: those with packs (most first), then the rest by name.</summary>
    public IReadOnlyList<PackLanguageOption> LanguageOptions =>
        [.. _languages.Where(l => LanguageQuery.Trim().Length == 0
            || TextSearch.Matches(l.Name, LanguageQuery.Trim())
            || TextSearch.Matches(l.EnglishName, LanguageQuery.Trim())
            || TextSearch.Matches(l.Tag, LanguageQuery.Trim()))];

    public bool HasNoLanguageMatch => LanguageOptions.Count == 0;

    /// <summary>Other languages with packs, for the empty state.</summary>
    public IReadOnlyList<PackLanguageOption> OtherLanguages => [.. _languages.Where(l => l.Count > 0 && l.Tag != Language)];

    public bool HasOtherLanguages => OtherLanguages.Count > 0;

    /// <summary>No pack list at all yet (first fetch running, registry never answered).</summary>
    public bool IsUnknown => _packIndex.Current?.Index is null;

    /// <summary>The list is the saved copy: banner, faded list, install off.</summary>
    public bool IsStale => _packIndex.Current is { IsStale: true, Index: not null };

    /// <summary>"Can't reach the registry. Showing the list saved on &lt;date&gt;; installing is unavailable…".</summary>
    public string OfflineText => _packIndex.Current is { IsStale: true, FetchedAt: { } at }
        ? _localizer.Format(nameof(Strings.Packs_Offline), at.ToLocalTime().ToString("g", _localizer.Culture))
        : string.Empty;

    public bool HasRows => Rows.Count > 0;

    /// <summary>The list with its details panel: registry packs of the language, or Mine rows.</summary>
    public bool ShowList => (!IsUnknown && HasRows) || HasMine;

    /// <summary>The Registry heading is shown above the registry rows when Mine has rows too.</summary>
    public bool ShowRegistryHeading => HasMine;

    /// <summary>Only Mine has rows: a line under the Registry heading says the registry has none.</summary>
    public bool ShowRegistryNone => HasMine && !HasRows && !IsUnknown;

    /// <summary>"No translation into Русский yet".</summary>
    public bool ShowEmpty => !IsUnknown && !HasRows && !HasMine;

    public string EmptyTitle => _localizer.Format(nameof(Strings.Game_Packs_None), LanguageName);

    /// <summary>"Русский: 3 packs".</summary>
    public string CountText => HasRows ? _localizer.Plural(nameof(Strings.Game_Packs_Count_One), Rows.Count, LanguageName) : string.Empty;

    /// <summary>Waits for the manifest fetches and checks running now (tests).</summary>
    internal Task WhenIdle
    {
        get
        {
            lock (_lock)
            {
                return _pump;
            }
        }
    }

    /// <summary>The tab is shown: start fetching manifests (not before, so a page opened on another tab costs nothing).</summary>
    public void Activate()
    {
        _active = true;
        Pump();
    }

    /// <summary>The page's game, status or target language changed, or a new pack list came.</summary>
    public void Update(Game game, LocalizationStatus status, string target)
    {
        var gameChanged = game.Version != _game.Version || game.IsAvailable != _game.IsAvailable || game.InstallPath != _game.InstallPath;
        _game = game;
        _status = status;
        if (target != _target)
        {
            // The list follows the "Translate games into" setting unless the player picked another language here.
            if (Language == _target)
            {
                Language = target;
            }

            _target = target;
        }

        if (gameChanged)
        {
            foreach (var row in _rowsByKey.Values)
            {
                row.ResetCheck();
            }
        }

        Rebuild();
    }

    /// <summary>The UI language changed: every text again.</summary>
    public void RefreshTexts()
    {
        // A new options list makes the dropdown clear its selection for a moment; _sort keeps the order.
        var sort = _sort;
        SortOptions = BuildSortOptions();
        OnPropertyChanged(nameof(SortOptions));
        SelectedSort = SortOptions.First(o => o.Value == sort);
        BuildLanguages();
        foreach (var row in _rowsByKey.Values)
        {
            row.RefreshTexts();
        }

        foreach (var row in Mine)
        {
            row.RefreshTexts();
        }

        OnPropertyChanged(string.Empty);
    }

    public void Close() => _closed.Cancel();

    /// <summary>An operation runs (any game: every <c>Install</c> is off), and the pack of this language being installed here.</summary>
    public void SetInstallState(bool busy, string? installingPackId)
    {
        if (busy == _busy && installingPackId == _installingPackId)
        {
            return;
        }

        _busy = busy;
        _installingPackId = installingPackId;
        var context = Context(Rows.Select(r => r.Pack).ToList());
        foreach (var row in Rows)
        {
            row.SetContext(context);
        }

        foreach (var row in Mine)
        {
            row.SetInstallState(busy, row.Pack is { } pack && pack.Manifest.Id == installingPackId);
        }
    }

    /// <summary>The result's <c>Show in Packs</c>, the installed block's for a local pack: that Mine row selected.</summary>
    public void ShowLocal(string language, string packId)
    {
        RebuildMine();
        if (Mine.FirstOrDefault(r => r.Pack is { } pack && pack.Manifest.Language == language && pack.Manifest.Id == packId) is { } row)
        {
            SelectMine(row);
        }
    }

    /// <summary>The game's local packs or job changed (a pack deleted, tried in game, a job started or ended).</summary>
    public void RefreshMine()
    {
        RebuildMine();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The installed block's <c>Show in Packs</c>: the pack's language, the pack selected.</summary>
    public void ShowPack(string language, string packId)
    {
        SelectLanguage(language);
        if (Rows.FirstOrDefault(r => r.Pack.Id == packId) is { } row)
        {
            Selected = row;
        }
    }

    [RelayCommand]
    private void SelectLanguage(string tag)
    {
        if (!GameLanguages.IsKnown(tag) || tag == Language)
        {
            return;
        }

        Language = tag;
        LanguageQuery = string.Empty;
        Rebuild();
    }

    /// <summary>Fetches the pack list again (offline banner).</summary>
    [RelayCommand(CanExecute = nameof(CanRetryRegistry))]
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

    private bool CanRetryRegistry() => !IsRetrying;

    [RelayCommand]
    private void OpenGeneration() => _openGeneration();

    partial void OnSelectedChanged(PackRowViewModel? value)
    {
        if (value is not null)
        {
            SelectedMine = null;
        }

        MarkSelected();
        Pump();
    }

    private void SelectRow(PackRowViewModel row)
    {
        SelectedMine = null;
        if (Selected == row)
        {
            MarkSelected();
        }
        else
        {
            Selected = row;
        }
    }

    private void SelectMine(MinePackRowViewModel row)
    {
        SelectedMine = row;
        MarkSelected();
    }

    /// <summary>One row is highlighted: the Mine row when one is selected, else the registry pack.</summary>
    private void MarkSelected()
    {
        foreach (var row in Rows)
        {
            row.IsSelected = SelectedMine is null && row == Selected;
        }

        foreach (var row in Mine)
        {
            row.IsSelected = row == SelectedMine;
        }

        OnPropertyChanged(nameof(ShowMineDetails));
        OnPropertyChanged(nameof(ShowPackDetails));
    }

    /// <summary>
    /// The Mine rows: the game's open job (any language), then its local packs; known rows are kept (their selection and
    /// install state), a selected row that is gone leaves the registry's selection.
    /// </summary>
    private void RebuildMine()
    {
        var rows = new List<MinePackRowViewModel>();
        var actions = new MinePackActions(SelectMine, _openGeneration, _installLocal, () => UninstallRequested?.Invoke(this, EventArgs.Empty), _deleteLocal, _publish);
        if (_jobs.FindOpen(_game.Id) is { } job)
        {
            var row = Mine.FirstOrDefault(r => r.Job?.Id == job.Id);
            if (row is null)
            {
                row = new MinePackRowViewModel(job, Language, _localizer, actions);
            }
            else
            {
                row.Update(job, Language);
            }

            rows.Add(row);
        }

        foreach (var pack in _localPacks.List(_game.Id))
        {
            var key = $"{pack.Manifest.Language}/{pack.Manifest.Id}";
            var installed = _localPacks.IsInstalled(pack);
            var row = Mine.FirstOrDefault(r => r.IsLocal && r.Key == key);
            if (row is null)
            {
                row = new MinePackRowViewModel(pack, Language, installed, _localizer, actions);
            }
            else
            {
                row.Update(pack, Language, installed);
            }

            row.SetInstallState(_busy, pack.Manifest.Id == _installingPackId);
            rows.Add(row);
        }

        Mine.Clear();
        foreach (var row in rows)
        {
            Mine.Add(row);
        }

        if (SelectedMine is { } selected && !rows.Contains(selected))
        {
            SelectedMine = null;
        }

        // With no registry pack to show, the first local pack is selected.
        if (SelectedMine is null && Selected is null && rows.FirstOrDefault(r => r.IsLocal) is { } first)
        {
            SelectedMine = first;
        }

        MarkSelected();
    }

    partial void OnSelectedSortChanged(PackSortOption value)
    {
        if (value is not null && value.Value != _sort)
        {
            _sort = value.Value;
            Rebuild();
        }
    }

    partial void OnLanguageQueryChanged(string value)
    {
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(HasNoLanguageMatch));
    }

    /// <summary>The rows of <see cref="Language"/> in the chosen order; known rows keep their check, the selection stays.</summary>
    private void Rebuild()
    {
        var index = _packIndex.Current?.Index;
        var packs = LocalizationStatuses.Rank(index?.PacksFor(_game.Id, Language) ?? [], _game);
        IEnumerable<RegistryPack> ordered = _sort switch
        {
            PackSort.Newest => packs.OrderByDescending(p => p.Updated),
            PackSort.Coverage => packs.OrderByDescending(p => p.Coverage.Ratio),
            _ => packs,
        };

        var context = Context(packs);
        var rows = new List<PackRowViewModel>();
        foreach (var pack in ordered)
        {
            var key = $"{pack.Language}/{pack.Id}/{pack.Revision}";
            if (_rowsByKey.TryGetValue(key, out var row))
            {
                row.SetPack(pack);
            }
            else
            {
                row = new PackRowViewModel(pack, _localizer, SelectRow, Retry, Install, Uninstall, _openGeneration, OpenUrl);
                _rowsByKey[key] = row;
            }

            row.SetContext(context);
            rows.Add(row);
        }

        var selectedId = Selected?.Pack.Id;
        var selectedLanguage = Selected?.Pack.Language;
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        BuildLanguages();
        var mineSelected = SelectedMine;
        var keep = selectedLanguage == Language ? rows.FirstOrDefault(r => r.Pack.Id == selectedId) : null;
        if (keep is not null && keep == Selected)
        {
            OnSelectedChanged(keep);
        }
        else
        {
            Selected = keep ?? rows.FirstOrDefault();
        }

        // A selected Mine row stays selected over a new registry list.
        SelectedMine = mineSelected;
        RebuildMine();
        OnPropertyChanged(string.Empty);
    }

    private PackRowContext Context(IReadOnlyList<RegistryPack> packs) =>
        new(IsStale, _status.Installed, InstalledTitle(packs), _busy, _installingPackId, _packIndex.Current?.Index?.Dependencies);

    private string? InstalledTitle(IReadOnlyList<RegistryPack> packs) => _status.Installed switch
    {
        { Local: true } local => local.Title ?? local.PackId,
        { } installed => packs.FirstOrDefault(p => p.Id == installed.PackId)?.Title ?? installed.PackId,
        _ => null,
    };

    private void BuildLanguages()
    {
        var game = _packIndex.Current?.Index?.Find(_game.Id);
        _languages =
        [
            .. GameLanguages.Tags
                .Select(tag => new PackLanguageOption(tag, LanguageNames.Native(tag), game?.PacksIn(tag).Count ?? 0, tag == Language, SelectLanguageFromPopover)
                {
                    EnglishName = EnglishName(tag),
                })
                .OrderByDescending(l => l.Count)
                .ThenBy(l => l.Name, LanguageNames.Order),
        ];
    }

    private void SelectLanguageFromPopover(string tag) => SelectLanguage(tag);

    private IReadOnlyList<PackSortOption> BuildSortOptions() =>
    [
        new(PackSort.StatusRating, _localizer[nameof(Strings.Packs_Sort_StatusRating)]),
        new(PackSort.Newest, _localizer[nameof(Strings.Packs_Sort_Newest)]),
        new(PackSort.Coverage, _localizer[nameof(Strings.Packs_Sort_Coverage)]),
    ];

    private void Retry(PackRowViewModel row)
    {
        if (row.State == PackCheckState.Failed)
        {
            row.ResetCheck();
            Pump();
        }
    }

    private void Install(PackRowViewModel row)
    {
        if (row.CanInstall)
        {
            InstallRequested?.Invoke(this, row);
        }
    }

    private void Uninstall(PackRowViewModel row)
    {
        if (row.ShowUninstall)
        {
            UninstallRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenUrl(Uri url)
    {
        if (!_shell.OpenUrl(url))
        {
            _logger.LogWarning("Couldn't open {Url}", url);
        }
    }

    /// <summary>Checks the rows one at a time, the selected one first, while the tab is active.</summary>
    private void Pump()
    {
        lock (_lock)
        {
            if (!_active || _pumping || _closed.IsCancellationRequested)
            {
                return;
            }

            _pumping = true;
        }

        var task = RunPumpAsync();
        lock (_lock)
        {
            if (!task.IsCompleted)
            {
                _pump = task;
            }
        }
    }

    private async Task RunPumpAsync()
    {
        while (true)
        {
            PackRowViewModel? row;
            lock (_lock)
            {
                row = _closed.IsCancellationRequested ? null
                    : Selected is { State: PackCheckState.NotChecked } selected ? selected
                    : Rows.FirstOrDefault(r => r.State == PackCheckState.NotChecked);
                if (row is null)
                {
                    _pumping = false;
                    return;
                }

                row.BeginCheck();
            }

            await CheckAsync(row);
        }
    }

    private async Task CheckAsync(PackRowViewModel row)
    {
        var game = _game;
        try
        {
            if (_packIndex.Current?.Location is not { } location)
            {
                row.SetFailed(new PackManifestFetch(null, PackManifestProblem.Offline, "No registry location."));
                return;
            }

            var manifest = row.Manifest;
            if (manifest is null)
            {
                var fetch = await _manifests.LoadAsync(location, row.Pack, _closed.Token);
                if (fetch.Manifest is null)
                {
                    row.SetFailed(fetch);
                    return;
                }

                manifest = fetch.Manifest;
                row.SetManifest(manifest);
            }

            var compatibility = await _manifests.CheckAsync(manifest, game, _closed.Token);
            if (game == _game)
            {
                row.SetChecked(compatibility);
            }
            else
            {
                // The game changed while comparing: compare again with the new one.
                row.ResetCheck();
            }
        }
        catch (OperationCanceledException)
        {
            row.ResetCheck();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Checking pack {Pack} for {Game} failed", row.Pack.Id, game.Id);
            row.SetFailed(new PackManifestFetch(null, PackManifestProblem.Invalid, e.Message));
        }
    }

    private static string EnglishName(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag, predefinedOnly: true).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }
}
