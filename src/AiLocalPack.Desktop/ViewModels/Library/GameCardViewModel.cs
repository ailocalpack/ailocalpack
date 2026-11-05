using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Toolkit.Engines;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Library;

/// <summary>
/// One cover card of the library grid (design §1): cover or placeholder, the status badge for the chosen language
/// (icon + text; faded while the pack list is the saved copy), a progress bar while generating, name, a context line
/// (likes and pack count / status, ETA, block reason or engine), the "manual" marker, the blocked look and the
/// status's quick action.
/// </summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private IBrush? _placeholderBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Cover), nameof(HasCover), nameof(Icon), nameof(HasIcon))]
    private CoverImage? _image;

    /// <param name="open">Opens the game page (card click, context menu).</param>
    /// <param name="openFolder">Opens the game folder in the file manager (context menu).</param>
    /// <param name="remove">Removes a manual game from the library (context menu); <c>null</c> = no such item.</param>
    /// <param name="quickAction">Runs the status's quick action (Install, Update, View, Resume, Generate); <c>null</c> = opens the game page.</param>
    public GameCardViewModel(
        Game game,
        Localizer localizer,
        Action<GameCardViewModel> open,
        Action<GameCardViewModel> openFolder,
        Action<GameCardViewModel>? remove = null,
        Action<GameCardViewModel>? quickAction = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(openFolder);
        Game = game;
        OpenCommand = new RelayCommand(() => open(this));
        OpenFolderCommand = new RelayCommand(() => openFolder(this), () => IsAvailable);
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this), () => remove is not null && IsManual);
        QuickActionCommand = new RelayCommand(() => (quickAction ?? open)(this), () => HasQuickAction);
        _localizer = localizer;
    }

    public Game Game { get; private set; }

    /// <summary>The game's status for the library's language (<see cref="ApplyStatus"/>).</summary>
    public LocalizationStatus Status { get; private set; } = LocalizationStatus.Unknown;

    public LocalizationStatusKind StatusKind => Status.Kind;

    /// <summary>The status comes from the saved pack list (registry unreachable): the badge is faded.</summary>
    public bool IsStale { get; private set; }

    /// <summary>On the card itself (not the page) so the context menu, which lives in its own popup, can reach it.</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>Disabled while the folder isn't available.</summary>
    public IRelayCommand OpenFolderCommand { get; }

    /// <summary>Removes the game from the library; only manual games (Steam games leave when uninstalled in Steam).</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>The status's primary action, shown over the cover on hover or keyboard focus.</summary>
    public IRelayCommand QuickActionCommand { get; }

    public string Name => Game.Name;

    /// <summary>Cover art, cropped to 2:3; <c>null</c> when there is none or the image is an icon.</summary>
    public Bitmap? Cover => Image is { IsIcon: false } image ? image.Bitmap : null;

    public bool HasCover => Cover is not null;

    /// <summary>A manual game's icon, drawn small on the placeholder (the name stays visible).</summary>
    public Bitmap? Icon => Image is { IsIcon: true } image ? image.Bitmap : null;

    public bool HasIcon => Icon is not null;

    /// <summary>
    /// Background of the placeholder cover (no art): a gradient with the game name on it. Made when the view first
    /// reads it (on the UI thread), so view models built elsewhere create no Avalonia objects: one made on a pool
    /// thread while a headless UI test sets Avalonia up can take the UI dispatcher from it.
    /// </summary>
    public IBrush PlaceholderBrush => _placeholderBrush ??= PlaceholderColors.CreateBrush(Game.Id);

    public bool IsManual => Game.Source == GameSource.Manual;

    public bool IsAvailable => Game.IsAvailable;

    /// <summary>Anti-cheat or opt-out blocks install and generation: the card is greyed, with a lock and a red badge.</summary>
    public bool IsBlocked => Status.IsBlocked;

    /// <summary>Unavailable (unplugged drive, folder gone) but not blocked: the card is dimmed, without the lock.</summary>
    public bool IsDimmed => !IsBlocked && !IsAvailable;

    /// <summary>No badge until the pack list is known (blocked games have theirs at once).</summary>
    public bool ShowBadge => StatusKind != LocalizationStatusKind.Unknown;

    /// <summary>Badge text: "Pack available", "Generating · 42%", "Anti-cheat"…</summary>
    public string BadgeText => StatusKind switch
    {
        LocalizationStatusKind.Installed => _localizer[nameof(Strings.Library_Status_Installed)],
        LocalizationStatusKind.PackAvailable => _localizer[nameof(Strings.Library_Status_PackAvailable)],
        LocalizationStatusKind.NoPack => _localizer[nameof(Strings.Library_Status_NoPack)],
        LocalizationStatusKind.NeedsUpdate => _localizer[nameof(Strings.Library_Status_NeedsUpdate)],
        LocalizationStatusKind.Generating => _localizer.Format(nameof(Strings.Library_Badge_Generating), Percent),
        LocalizationStatusKind.Paused => _localizer.Format(nameof(Strings.Library_Badge_Paused), Percent),
        LocalizationStatusKind.Blocked when Status.Block == LocalizationBlock.AntiCheat => _localizer[nameof(Strings.Library_Badge_AntiCheat)],
        LocalizationStatusKind.Blocked => _localizer[nameof(Strings.Library_Badge_Unavailable)],
        _ => string.Empty,
    };

    /// <summary>Resource key of the badge icon geometry.</summary>
    public string BadgeIcon => StatusKind switch
    {
        LocalizationStatusKind.Installed => "IconCheck",
        LocalizationStatusKind.PackAvailable => "IconDownload",
        LocalizationStatusKind.NeedsUpdate => "IconAlert",
        LocalizationStatusKind.Generating => "IconLoader",
        LocalizationStatusKind.Paused => "IconPause",
        LocalizationStatusKind.Blocked => "IconShield",
        _ => "IconCircleDashed",
    };

    public bool IsToneOk => StatusKind == LocalizationStatusKind.Installed;

    public bool IsToneAccent => StatusKind == LocalizationStatusKind.PackAvailable;

    public bool IsToneNone => StatusKind == LocalizationStatusKind.NoPack;

    public bool IsToneWarn => StatusKind == LocalizationStatusKind.NeedsUpdate;

    public bool IsToneGen => StatusKind is LocalizationStatusKind.Generating or LocalizationStatusKind.Paused;

    public bool IsToneBad => IsBlocked;

    /// <summary>Generation progress, 0–100.</summary>
    public int Percent => Math.Clamp(Status.Job?.Percent ?? 0, 0, 100);

    /// <summary>A progress bar along the bottom of the cover while a job exists (striped when paused).</summary>
    public bool ShowProgress => IsToneGen;

    public bool IsPaused => StatusKind == LocalizationStatusKind.Paused;

    public bool HasQuickAction => QuickActionText.Length > 0;

    /// <summary>"Install", "Update", "View", "Resume", "Generate with AI"; empty for installed, blocked and unknown.</summary>
    public string QuickActionText => StatusKind switch
    {
        LocalizationStatusKind.PackAvailable => _localizer[nameof(Strings.Library_Action_Install)],
        LocalizationStatusKind.NeedsUpdate => _localizer[nameof(Strings.Library_Action_Update)],
        LocalizationStatusKind.Generating => _localizer[nameof(Strings.Library_Action_View)],
        LocalizationStatusKind.Paused => _localizer[nameof(Strings.Library_Action_Resume)],
        LocalizationStatusKind.NoPack => _localizer[nameof(Strings.Library_Action_Generate)],
        _ => string.Empty,
    };

    public string QuickActionIcon => StatusKind switch
    {
        LocalizationStatusKind.PackAvailable => "IconDownload",
        LocalizationStatusKind.NeedsUpdate => "IconRefresh",
        LocalizationStatusKind.Generating => "IconLoader",
        LocalizationStatusKind.Paused => "IconPlay",
        _ => "IconSparkles",
    };

    /// <summary>The primary (accent) look for Install; Generate is quiet (ghost); Update amber; View / Resume teal.</summary>
    public bool IsQuickPrimary => StatusKind == LocalizationStatusKind.PackAvailable;

    public bool IsQuickWarn => StatusKind == LocalizationStatusKind.NeedsUpdate;

    public bool IsQuickGen => IsToneGen;

    public bool IsQuickGhost => StatusKind == LocalizationStatusKind.NoPack;

    public string ManualText => _localizer[nameof(Strings.Library_Card_Manual)];

    /// <summary>Likes of the best pack (installed, available, needs update), with a thumbs-up; hidden when none.</summary>
    public bool ShowLikes => ShowsPackInfo && Status.BestPack is { Rating.Likes: > 0 };

    public string LikesText => (Status.BestPack?.Rating.Likes ?? 0).ToString("N0", _localizer.Culture);

    /// <summary>The "·" between the likes and the context line.</summary>
    public bool ShowLikesSeparator => ShowLikes && ContextLine.Length > 0;

    /// <summary>The "·" between the context line and the "manual" marker.</summary>
    public bool ShowManualSeparator => IsManual && (ContextLine.Length > 0 || ShowLikes);

    /// <summary>
    /// Line under the name, by status: the block reason (anti-cheat name, "studio opted out"); "folder not available";
    /// for a pack status the pack count ("3 packs") or the best pack's status (verified / alpha); the ETA or pause
    /// reason while generating; else the detected engine.
    /// </summary>
    public string ContextLine
    {
        get
        {
            if (IsBlocked)
            {
                return Status.Block == LocalizationBlock.AntiCheat
                    ? Status.AntiCheatName ?? string.Empty
                    : _localizer[nameof(Strings.Library_Card_OptedOut)];
            }

            if (!IsAvailable)
            {
                return _localizer[nameof(Strings.Library_Card_Unavailable)];
            }

            if (ShowsPackInfo && Status.BestPack is { } best)
            {
                return Status.Packs.Count > 1
                    ? _localizer.Plural(nameof(Strings.Library_Card_PackCount_One), Status.Packs.Count)
                    : _localizer[best.Status == PackStatus.Verified ? nameof(Strings.Library_Card_Verified) : nameof(Strings.Library_Card_Alpha)];
            }

            if (Status.Job is { } job)
            {
                return job switch
                {
                    { IsPaused: true, PauseReason: { } reason } => reason,
                    { IsPaused: false, Remaining: { } left } => _localizer.Format(nameof(Strings.Library_Card_Remaining), Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))),
                    _ => string.Empty,
                };
            }

            if (!EngineKind.TryParse(Game.Engine, out var engine))
            {
                // Not detected yet (the scan is still running): the line stays empty instead of guessing.
                return string.Empty;
            }

            return engine.Engine == GameEngine.Unknown ? _localizer[nameof(Strings.Library_Card_EngineUnknown)] : engine.DisplayName;
        }
    }

    /// <summary>Screen reader name: the game name with its status (and the reason when blocked).</summary>
    public string AccessibleName => StatusKind switch
    {
        LocalizationStatusKind.Unknown => Name,
        LocalizationStatusKind.Blocked => _localizer.Format(nameof(Strings.Library_Card_BlockedName), Name, ContextLine),
        _ => _localizer.Format(nameof(Strings.Library_Card_StatusName), Name, BadgeText),
    };

    private bool ShowsPackInfo => StatusKind is LocalizationStatusKind.Installed or LocalizationStatusKind.PackAvailable or LocalizationStatusKind.NeedsUpdate;

    /// <summary>Takes the catalog's newer state of the same game (engine detected, renamed, drive plugged in).</summary>
    public void Update(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Id != Game.Id)
        {
            throw new ArgumentException("A card shows one game; its ID can't change.", nameof(game));
        }

        if (game == Game)
        {
            return;
        }

        Game = game;
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsDimmed));
        RefreshTexts();
        OpenFolderCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows a newly computed status (language switch, new pack list, catalog change).</summary>
    /// <param name="stale">The status comes from the saved copy of the pack list.</param>
    public void ApplyStatus(LocalizationStatus status, bool stale)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status == Status && stale == IsStale)
        {
            return;
        }

        Status = status;
        IsStale = stale;
        OnPropertyChanged(string.Empty);
        QuickActionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-raises the texts after a UI language switch.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ContextLine));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(QuickActionText));
        OnPropertyChanged(nameof(ManualText));
        OnPropertyChanged(nameof(LikesText));
        OnPropertyChanged(nameof(ShowLikesSeparator));
        OnPropertyChanged(nameof(ShowManualSeparator));
    }

    /// <summary>Loads the cover; a failure leaves the placeholder.</summary>
    public async Task LoadCoverAsync(ICoverLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        try
        {
            if (await loader.LoadAsync(Game, cancellationToken) is { } image)
            {
                Image = image;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
