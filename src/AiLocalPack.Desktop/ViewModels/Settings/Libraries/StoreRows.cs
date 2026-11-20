using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Stores;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings.Libraries;

/// <summary>Store names, marks and apps as the UI shows them (task 83).</summary>
public static class StoreTexts
{
    /// <summary>The store's name (a brand: the same in every language, kept in the resources all the same).</summary>
    public static string NameKey(GameSource source) => source switch
    {
        GameSource.Steam => nameof(Strings.Settings_Steam_Label),
        GameSource.Epic => nameof(Strings.Settings_Group_Epic),
        GameSource.Gog => nameof(Strings.Settings_Group_Gog),
        GameSource.Ea => nameof(Strings.Settings_Group_Ea),
        _ => nameof(Strings.Settings_Group_ManualGames),
    };

    /// <summary>The store's mark in <c>BrandIcons.axaml</c>.</summary>
    public static string BrandKey(GameSource source) => source switch
    {
        GameSource.Steam => "BrandSteam",
        GameSource.Epic => "BrandEpicGames",
        GameSource.Gog => "BrandGog",
        GameSource.Ea => "BrandEa",
        _ => "IconFolder",
    };

    /// <summary>
    /// What "Play via …" names for a game a store starts (Steam, Epic Games, Heroic); <c>null</c> when the game's own
    /// executable starts it.
    /// </summary>
    public static string? ViaKey(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (Core.Launch.GameLaunchPlanner.StoreUrl(game) is null)
        {
            return null;
        }

        return game.App is { } app && app != StoreApps.EpicLauncher ? AppKey(app) : NameKey(game.Source);
    }

    /// <summary>The app that installed games (<see cref="StoreApps"/>).</summary>
    public static string AppKey(string app) => app switch
    {
        StoreApps.EpicLauncher => nameof(Strings.Settings_StoreApp_EpicLauncher),
        StoreApps.Heroic => nameof(Strings.Settings_StoreApp_Heroic),
        StoreApps.Legendary => nameof(Strings.Settings_StoreApp_Legendary),
        StoreApps.GogGalaxy or StoreApps.GogInstaller => nameof(Strings.Settings_StoreApp_Galaxy),
        _ => nameof(Strings.Settings_StoreApp_EaApp),
    };
}

/// <summary>What a store's summary row shows (<see cref="StoreSummaryRow"/>).</summary>
/// <param name="IsEnabled">The player left the store on.</param>
/// <param name="IsScanning">This store is being scanned right now.</param>
/// <param name="IsWaiting">A refresh runs and hasn't reached this store yet (none finished before).</param>
/// <param name="ScannedAt">When the last scan of this session finished.</param>
/// <param name="Scan">The store's last scan; <c>null</c> before the first.</param>
/// <param name="Games">The store's games shown in the library (available ones).</param>
public sealed record StoreSummaryState(bool IsEnabled, bool IsScanning, bool IsWaiting, DateTimeOffset? ScannedAt, StoreScan? Scan, int Games);

/// <summary>
/// A store's card head in Settings → Libraries (design §8, store cards): mark, "Epic Games Launcher · 14 games", "Reads
/// … · scanned at 14:02", <c>Rescan</c> and the switch that shows its games in the library or hides them
/// (<see cref="AppSettings.DisabledStores"/>; switching rescans). Off: "Turned off" with what that means.
/// </summary>
public sealed class StoreSummaryRow : SettingRow
{
    private readonly Func<Task> _rescan;
    private StoreSummaryState _state;

    public StoreSummaryRow(SettingRowServices services, GameSource source, Func<Task> rescan)
        : base(services, StoreTexts.NameKey(source), nameof(Strings.Settings_Stores_Keywords))
    {
        ArgumentNullException.ThrowIfNull(rescan);
        Source = source;
        _rescan = rescan;
        _state = new StoreSummaryState(Settings.IsStoreEnabled(source), false, false, null, null, 0);
        RescanCommand = new AsyncRelayCommand(rescan, () => !IsBusy && IsEnabled);
    }

    public GameSource Source { get; }

    public string BrandKey => StoreTexts.BrandKey(Source);

    public string Name => Localizer[StoreTexts.NameKey(Source)];

    public IAsyncRelayCommand RescanCommand { get; }

    public bool IsBusy => _state.IsScanning || _state.IsWaiting;

    public bool IsScanning => _state.IsScanning;

    /// <summary>The switch: on shows the store's games in the library. Setting it saves and rescans.</summary>
    public bool IsEnabled
    {
        get => Settings.IsStoreEnabled(Source);
        set
        {
            if (value == IsEnabled)
            {
                return;
            }

            Save(s => s.WithStore(Source, value));
            _ = _rescan();
        }
    }

    public string SwitchName => Localizer.Format(nameof(Strings.Settings_Store_Show), Name);

    public string RescanText => Localizer[IsScanning ? nameof(Strings.Settings_Scanning) : nameof(Strings.Settings_Rescan)];

    public bool HasProblem => _state.IsEnabled && _state.Scan?.Problem is not null;

    public override string Label => _state switch
    {
        { IsEnabled: false } => Localizer[nameof(Strings.Settings_Store_Off)],
        { IsScanning: true } => Localizer[nameof(Strings.Settings_Store_Scanning)],
        { IsWaiting: true } => Localizer[nameof(Strings.Settings_Store_Waiting)],
        { Scan: null } => Name,
        _ => Localizer.Format(
            nameof(Strings.Settings_Store_Summary),
            Apps(),
            Localizer.Plural(nameof(Strings.Library_GameCount_One), _state.Games)),
    };

    public override string? Help => _state switch
    {
        { IsEnabled: false } => Localizer[nameof(Strings.Settings_Store_OffHelp)],
        { Scan.Problem: { } problem } => problem,
        { Scan.ReadFrom: { } from, ScannedAt: { } at } => Localizer.Format(
            nameof(Strings.Settings_Store_Reads), from, at.ToLocalTime().ToString("t", Localizer.Culture)),
        _ => null,
    };

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Name];

    /// <summary>Shows a new state (from the section, on the UI thread).</summary>
    public void Update(StoreSummaryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state == _state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(RescanText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HasProblem));
        RescanCommand.NotifyCanExecuteChanged();
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        base.OnSettingsChanged(settings);
        OnPropertyChanged(nameof(IsEnabled));
        RescanCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SwitchName));
        OnPropertyChanged(nameof(RescanText));
    }

    /// <summary>"Epic Games Launcher" or "Epic Games Launcher, Heroic": the apps the store's games came from.</summary>
    private string Apps()
    {
        // The store's own app first (the order StoreApps lists them), then Heroic and Legendary.
        string[] order = [StoreApps.EpicLauncher, StoreApps.GogGalaxy, StoreApps.GogInstaller, StoreApps.EaApp, StoreApps.Heroic, StoreApps.Legendary];
        var apps = (_state.Scan?.Games ?? []).Select(g => g.App).Distinct()
            .OrderBy(a => Array.IndexOf(order, a) is var i and >= 0 ? i : order.Length)
            .Select(a => Localizer[StoreTexts.AppKey(a)]).Distinct().ToList();
        return apps.Count > 0 ? string.Join(", ", apps) : Name;
    }
}

/// <summary>
/// A folder a store installed games into: path, "9 games · Epic Games Launcher", <c>Open</c>; not there right now (an
/// unplugged drive): ⚠ <em>Not available</em> with how many games are hidden.
/// </summary>
public sealed class StoreFolderRow : SettingRow
{
    public StoreFolderRow(SettingRowServices services, string path, Action<StoreFolderRow> open)
        : base(services, nameof(Strings.Settings_SteamFolder_Label), null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(open);
        Path = path;
        OpenCommand = new RelayCommand(() => open(this), () => IsAvailable);
    }

    public string Path { get; }

    public bool IsAvailable { get; private set; } = true;

    public int GameCount { get; private set; }

    /// <summary>The app that installed the folder's games (<see cref="StoreApps"/>).</summary>
    public string App { get; private set; } = StoreApps.EpicLauncher;

    public IRelayCommand OpenCommand { get; }

    public override string Label => Path;

    public override string? Help => IsAvailable
        ? Localizer.Format(nameof(Strings.Settings_StoreFolder_Help), Localizer.Plural(nameof(Strings.Library_GameCount_One), GameCount), Localizer[StoreTexts.AppKey(App)])
        : Localizer.Plural(nameof(Strings.Settings_SteamFolder_Hidden_One), GameCount);

    public string NotAvailableText => Localizer[nameof(Strings.Settings_SteamFolder_NotAvailable)];

    public string OpenText => Localizer[nameof(Strings.Settings_Open)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Path];

    public void Update(StoreFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if ((IsAvailable, GameCount, App) == (folder.IsAvailable, folder.Games, folder.App))
        {
            return;
        }

        (IsAvailable, GameCount, App) = (folder.IsAvailable, folder.Games, folder.App);
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(Help));
        OpenCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(NotAvailableText));
        OnPropertyChanged(nameof(OpenText));
    }
}

/// <summary>A store this OS supports that isn't on this computer: its mark, "The Epic Games Launcher and Heroic aren't installed", what to do.</summary>
public sealed class StoreAbsentRow(SettingRowServices services, GameSource source)
    : SettingRow(services, NotFoundKey(source), nameof(Strings.Settings_Stores_Keywords))
{
    public GameSource Source { get; } = source;

    public string BrandKey => StoreTexts.BrandKey(Source);

    public string Name => Localizer[StoreTexts.NameKey(Source)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Name];

    protected override string? HelpKey(AppSettings settings) => nameof(Strings.Settings_Store_NotFound_Help);

    private static string NotFoundKey(GameSource source) => source switch
    {
        GameSource.Epic => nameof(Strings.Settings_Store_NotFound_Epic),
        GameSource.Gog => nameof(Strings.Settings_Store_NotFound_Gog),
        _ => nameof(Strings.Settings_Store_NotFound_Ea),
    };
}
