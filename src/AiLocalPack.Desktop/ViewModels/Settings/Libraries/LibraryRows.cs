using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Toolkit.Engines;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings.Libraries;

/// <summary>
/// Steam summary row of Settings → Libraries: "Steam · 2 folders · 96 games", the last scan time and <c>Rescan</c>;
/// "scanning…" while a scan runs, "Steam not found" when there is no library at all.
/// </summary>
public sealed class SteamSummaryRow : SettingRow
{
    private SteamSummaryState _state = SteamSummaryState.NotScanned;

    public SteamSummaryRow(SettingRowServices services, Func<Task> rescan)
        : base(services, nameof(Strings.Settings_Steam_Label), nameof(Strings.Settings_Steam_Keywords))
    {
        ArgumentNullException.ThrowIfNull(rescan);
        RescanCommand = new AsyncRelayCommand(rescan, () => !IsScanning);
    }

    public IAsyncRelayCommand RescanCommand { get; }

    public bool IsScanning => _state.IsScanning;

    public string RescanText => Localizer[IsScanning ? nameof(Strings.Settings_Scanning) : nameof(Strings.Settings_Rescan)];

    public override string Label => _state switch
    {
        { IsScanning: true } => Localizer[nameof(Strings.Settings_Steam_Scanning)],
        { ScannedAt: null } => Localizer[nameof(Strings.Settings_Steam_Label)],
        { Folders: 0 } => Localizer[nameof(Strings.Settings_Steam_NotFound)],
        _ => Localizer.Format(
            nameof(Strings.Settings_Steam_Summary),
            Localizer.Plural(nameof(Strings.Settings_Steam_Folders_One), _state.Folders),
            Localizer.Plural(nameof(Strings.Library_GameCount_One), _state.Games)),
    };

    public override string? Help => _state switch
    {
        { IsScanning: true } => Localizer[nameof(Strings.Settings_Steam_ScanningHelp)],
        { ScannedAt: null } => Localizer[nameof(Strings.Settings_Steam_NotScanned)],
        { Folders: 0 } => Localizer[nameof(Strings.Settings_Steam_NotFoundHelp)],
        { ScannedAt: { } at } => Localizer.Format(nameof(Strings.Settings_Steam_LastScan), at.ToLocalTime().ToString("t", Localizer.Culture)),
    };

    /// <summary>Shows a new scan state (from the section, on the UI thread).</summary>
    public void Update(SteamSummaryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state == _state)
        {
            return;
        }

        _state = state;
        RaiseTexts();
        OnPropertyChanged(nameof(IsScanning));
        RescanCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(RescanText));
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(RescanText));
    }
}

/// <summary>What the Steam summary row shows.</summary>
/// <param name="IsScanning">A scan is running.</param>
/// <param name="ScannedAt">When the last scan of this session finished; <c>null</c> = none yet.</param>
/// <param name="Folders">Library folders in the last scan (available or not).</param>
/// <param name="Games">Steam games shown in the library (available ones).</param>
public sealed record SteamSummaryState(bool IsScanning, DateTimeOffset? ScannedAt, int Folders, int Games)
{
    public static SteamSummaryState NotScanned { get; } = new(false, null, 0, 0);
}

/// <summary>
/// One Steam library folder: path, game count and where it came from (found by Steam / added by you), <c>Open</c>;
/// <c>Remove</c> for folders added by hand. A folder on a missing drive: ⚠ <em>Not available</em>, its games hidden,
/// <c>Rescan</c> / <c>Remove</c>.
/// </summary>
public sealed class LibraryFolderRow : SettingRow
{
    public LibraryFolderRow(
        SettingRowServices services,
        string path,
        Action<LibraryFolderRow> open,
        Action<LibraryFolderRow> remove,
        IAsyncRelayCommand rescan)
        : base(services, nameof(Strings.Settings_SteamFolder_Label), null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(remove);
        Path = path;
        OpenCommand = new RelayCommand(() => open(this), () => IsAvailable);
        RemoveCommand = new RelayCommand(() => remove(this), () => CanRemove);
        RescanCommand = rescan ?? throw new ArgumentNullException(nameof(rescan));
    }

    public string Path { get; }

    public bool IsAvailable { get; private set; } = true;

    public bool IsAddedByHand { get; private set; }

    /// <summary>Steam games of this folder in the catalog (hidden ones included while it's not available).</summary>
    public int GameCount { get; private set; }

    /// <summary>Folders Steam found can't be removed while they work: Steam itself lists them.</summary>
    public bool CanRemove => IsAddedByHand || !IsAvailable;

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    /// <summary>Shown on a folder that isn't available.</summary>
    public IAsyncRelayCommand RescanCommand { get; }

    public override string Label => Path;

    public override string? Help => IsAvailable
        ? Localizer.Format(
            IsAddedByHand ? nameof(Strings.Settings_SteamFolder_Added) : nameof(Strings.Settings_SteamFolder_Found),
            Localizer.Plural(nameof(Strings.Library_GameCount_One), GameCount))
        : Localizer.Plural(nameof(Strings.Settings_SteamFolder_Hidden_One), GameCount);

    public string NotAvailableText => Localizer[nameof(Strings.Settings_SteamFolder_NotAvailable)];

    public string OpenText => Localizer[nameof(Strings.Settings_Open)];

    public string RemoveText => Localizer[nameof(Strings.Settings_Remove)];

    public string RescanText => Localizer[nameof(Strings.Settings_Rescan)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Path];

    public void Update(bool isAvailable, bool isAddedByHand, int gameCount)
    {
        if ((IsAvailable, IsAddedByHand, GameCount) == (isAvailable, isAddedByHand, gameCount))
        {
            return;
        }

        (IsAvailable, IsAddedByHand, GameCount) = (isAvailable, isAddedByHand, gameCount);
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsAddedByHand));
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(Help));
        OpenCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(NotAvailableText));
        OnPropertyChanged(nameof(OpenText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(RescanText));
    }
}

/// <summary>A manually added game: name, path and engine, <c>Open</c>, <c>Remove</c> (confirmation).</summary>
public sealed class ManualGameRow : SettingRow
{
    public ManualGameRow(SettingRowServices services, Game game, Action<ManualGameRow> open, Action<ManualGameRow> remove)
        : base(services, nameof(Strings.Settings_ManualGame_Label), null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(remove);
        Game = game;
        OpenCommand = new RelayCommand(() => open(this), () => Game.IsAvailable);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public Game Game { get; private set; }

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public override string Label => Game.Name;

    /// <summary>The folder; shown in a monospace line with <see cref="Engine"/> after it.</summary>
    public string Path => Game.InstallPath;

    /// <summary>Engine name, "Unknown engine", "Folder not available"; empty while not detected yet.</summary>
    public string Engine
    {
        get
        {
            if (!Game.IsAvailable)
            {
                return Localizer[nameof(Strings.Library_Card_Unavailable)];
            }

            if (!EngineKind.TryParse(Game.Engine, out var engine))
            {
                return string.Empty;
            }

            return engine.Engine == GameEngine.Unknown ? Localizer[nameof(Strings.Library_Card_EngineUnknown)] : engine.DisplayName;
        }
    }

    public bool HasEngine => Engine.Length > 0;

    public bool IsAvailable => Game.IsAvailable;

    public string OpenText => Localizer[nameof(Strings.Settings_Open)];

    public string RemoveText => Localizer[nameof(Strings.Settings_Remove)];

    public override string? Help => null;

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Game.Name, Game.InstallPath];

    public void Update(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game == Game)
        {
            return;
        }

        Game = game;
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Engine));
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(IsAvailable));
        OpenCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(Engine));
        OnPropertyChanged(nameof(OpenText));
        OnPropertyChanged(nameof(RemoveText));
    }
}

/// <summary>
/// A row that is one button ("+ Add Steam library folder", "Add game folder") with a quiet hint, and an error line when
/// the action failed (not a Steam library, already listed).
/// </summary>
public sealed class ActionSettingRow : SettingRow
{
    private readonly string? _helpKey;
    private string? _error;

    public ActionSettingRow(SettingRowServices services, string labelKey, string? helpKey, string? keywordsKey, string iconKey, Func<Task> action)
        : base(services, labelKey, keywordsKey)
    {
        ArgumentNullException.ThrowIfNull(action);
        _helpKey = helpKey;
        IconKey = iconKey;
        Command = new AsyncRelayCommand(action);
    }

    /// <summary>Resource key of the button's icon geometry.</summary>
    public string IconKey { get; }

    public IAsyncRelayCommand Command { get; }

    /// <summary>Why the last attempt failed, in the UI language; <c>null</c> when it didn't.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    protected override string? HelpKey(Core.Settings.AppSettings settings) => _helpKey;
}

/// <summary>A row with only an explanation (no manually added games yet).</summary>
public sealed class TextSettingRow(SettingRowServices services, string textKey) : SettingRow(services, textKey, null)
{
    public override string? Help => null;
}

/// <summary>A feature that comes later (other stores): label, explanation, a "Coming soon" pill.</summary>
public sealed class ComingSoonSettingRow(SettingRowServices services, string labelKey, string helpKey, string? keywordsKey)
    : SettingRow(services, labelKey, keywordsKey)
{
    public string PillText => Localizer[nameof(Strings.Settings_ComingSoonPill)];

    protected override string? HelpKey(Core.Settings.AppSettings settings) => helpKey;

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(PillText));
    }
}
