using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.GamePage;
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
        QuickActionCommand = new RelayCommand(() => (quickAction ?? open)(this), () => HasQuickAction && !IsInstallBusy);
        _localizer = localizer;
    }

    public Game Game { get; private set; }

    /// <summary>The library's target language (the local pack's line names its language when it differs).</summary>
    public string LibraryLanguage { get; set; } = string.Empty;

    /// <summary>The clock for a paused job's resume time (the player's zone).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The game's status for the library's language (<see cref="ApplyStatus"/>).</summary>
    public LocalizationStatus Status { get; private set; } = LocalizationStatus.Unknown;

    /// <summary>
    /// The status the card shows (badge, filters, sort, quick action): the game's, except for the last generation's local
    /// pack (task 55) when nothing else is installed and no job runs: waiting = like a pack available (<c>Install &amp;
    /// play</c>), installed in another language than the library's = installed.
    /// </summary>
    public LocalizationStatusKind StatusKind => LocalResult switch
    {
        { Installed: true } => LocalizationStatusKind.Installed,
        { } => LocalizationStatusKind.PackAvailable,
        _ => Status.Kind,
    };

    /// <summary>The last generation's local pack the card stands for (<see cref="StatusKind"/>), else <c>null</c>.</summary>
    public GenerationResultState? LocalResult => GamePrimaryActions.Resolve(Status) is
        { Kind: GamePrimaryActionKind.InstallLocalPack or GamePrimaryActionKind.Play, Result: { } result } ? result : null;

    /// <summary>The status comes from the saved pack list (registry unreachable): the badge is faded.</summary>
    public bool IsStale { get; private set; }

    /// <summary>The install, re-apply or uninstall running on this game (<see cref="ApplyOperation"/>), if any.</summary>
    public InstallOperation? Operation { get; private set; }

    /// <summary>An operation runs on this game: the badge reads "Installing 40%" with a bar along the cover.</summary>
    public bool IsInstalling => Operation is not null;

    /// <summary>An operation runs on some game (one at a time): the quick action is off.</summary>
    public bool IsInstallBusy { get; private set; }

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
    public bool ShowBadge => StatusKind != LocalizationStatusKind.Unknown || IsInstalling;

    /// <summary>Badge text: "Pack available", "Generating · 42%", "Installing 40%", "Anti-cheat"…</summary>
    public string BadgeText => Operation switch
    {
        { Kind: InstallOperationKind.Uninstall } => _localizer[nameof(Strings.Library_Badge_Uninstalling)],
        { } operation => _localizer.Format(nameof(Strings.Library_Badge_Installing), operation.Percent),
        _ when LocalResult is { Installed: false } => _localizer[nameof(Strings.Library_Badge_LocalReady)],
        _ => StatusBadgeText,
    };

    private string StatusBadgeText => StatusKind switch
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
    public string BadgeIcon => IsInstalling ? "IconLoader" : LocalResult is { Installed: false } ? "IconCheckCircle" : StatusKind switch
    {
        LocalizationStatusKind.Installed => "IconCheck",
        LocalizationStatusKind.PackAvailable => "IconDownload",
        LocalizationStatusKind.NeedsUpdate => "IconAlert",
        LocalizationStatusKind.Generating => "IconLoader",
        LocalizationStatusKind.Paused => "IconPause",
        LocalizationStatusKind.Blocked => "IconShield",
        _ => "IconCircleDashed",
    };

    public bool IsToneOk => !IsInstalling && StatusKind == LocalizationStatusKind.Installed;

    public bool IsToneAccent => IsInstalling || StatusKind == LocalizationStatusKind.PackAvailable;

    public bool IsToneNone => !IsInstalling && StatusKind == LocalizationStatusKind.NoPack;

    public bool IsToneWarn => !IsInstalling && StatusKind == LocalizationStatusKind.NeedsUpdate;

    public bool IsToneGen => !IsInstalling && StatusKind is LocalizationStatusKind.Generating or LocalizationStatusKind.Paused;

    public bool IsToneBad => IsBlocked;

    /// <summary>Install progress while installing, else generation progress, 0–100.</summary>
    public int Percent => Operation?.Percent ?? Math.Clamp(Status.Job?.Percent ?? 0, 0, 100);

    /// <summary>A progress bar along the bottom of the cover while installing or while a job exists (striped when paused).</summary>
    public bool ShowProgress => IsInstalling || IsToneGen;

    public bool IsPaused => !IsInstalling && StatusKind == LocalizationStatusKind.Paused;

    /// <summary>None while an operation runs on this game.</summary>
    public bool HasQuickAction => !IsInstalling && QuickActionText.Length > 0;

    /// <summary>
    /// "Install", "Update" ("Re-apply" for a pack that still fits), "View", "Resume" (paused or interrupted job), "Retry"
    /// (failed stage), "Generate with AI"; empty for installed, blocked and unknown.
    /// </summary>
    public string QuickActionText => LocalResult is { Installed: false } ? _localizer[nameof(Strings.Game_Action_InstallPlay)] : StatusKind switch
    {
        LocalizationStatusKind.PackAvailable => _localizer[nameof(Strings.Library_Action_Install)],
        LocalizationStatusKind.NeedsUpdate => _localizer[GamePrimaryActions.Resolve(Status).Kind == GamePrimaryActionKind.Reapply
            ? nameof(Strings.Library_Action_Reapply)
            : nameof(Strings.Library_Action_Update)],
        LocalizationStatusKind.Generating => _localizer[nameof(Strings.Library_Action_View)],
        LocalizationStatusKind.Paused => _localizer[GamePrimaryActions.JobKind(Status.Job) switch
        {
            GamePrimaryActionKind.ResumeGeneration => nameof(Strings.Library_Action_Resume),
            GamePrimaryActionKind.RetryStage => nameof(Strings.Library_Action_Retry),
            _ => nameof(Strings.Library_Action_View),
        }],
        LocalizationStatusKind.NoPack => _localizer[nameof(Strings.Library_Action_Generate)],
        _ => string.Empty,
    };

    public string QuickActionIcon => LocalResult is { Installed: false } ? "IconPlay" : StatusKind switch
    {
        LocalizationStatusKind.PackAvailable => "IconDownload",
        LocalizationStatusKind.NeedsUpdate => "IconRefresh",
        LocalizationStatusKind.Generating => "IconLoader",
        LocalizationStatusKind.Paused => GamePrimaryActions.JobKind(Status.Job) switch
        {
            GamePrimaryActionKind.ResumeGeneration => "IconPlay",
            GamePrimaryActionKind.RetryStage => "IconRefresh",
            _ => "IconPencil",
        },
        _ => "IconSparkles",
    };

    /// <summary>The primary (accent) look for Install; Generate is quiet (ghost); Update amber; View / Resume teal.</summary>
    public bool IsQuickPrimary => StatusKind == LocalizationStatusKind.PackAvailable;

    public bool IsQuickWarn => StatusKind == LocalizationStatusKind.NeedsUpdate;

    public bool IsQuickGen => IsToneGen;

    public bool IsQuickGhost => StatusKind == LocalizationStatusKind.NoPack;

    public string ManualText => _localizer[nameof(Strings.Library_Card_Manual)];

    /// <summary>Likes of the best pack (installed, available, needs update), with a thumbs-up; hidden when none.</summary>
    public bool ShowLikes => LocalResult is null && ShowsPackInfo && Status.BestPack is { Rating.Likes: > 0 };

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

            if (LocalResult is { } local)
            {
                // "Your pack · 98% of strings", or "Your pack · Русский" when it isn't the library's language (the card is narrow).
                return local.Language == LibraryLanguage
                    ? _localizer.Format(nameof(Strings.Library_Card_LocalPack), Math.Floor(local.Coverage * 100).ToString("0", _localizer.Culture))
                    : _localizer.Format(nameof(Strings.Library_Card_LocalPackLanguage), Core.Languages.LanguageNames.Native(local.Language));
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
                    { Phase: GenerationJobPhase.Running, Remaining: { } left } => _localizer.Format(nameof(Strings.Library_Card_Remaining), GenerationTexts.Duration(left, _localizer)),
                    { Phase: GenerationJobPhase.PausedByLimit, ResumeAt: { } at } => _localizer.Format(nameof(Strings.Library_Card_PausedLimit), GenerationTexts.ClockTime(at, Time, _localizer)),
                    { Phase: GenerationJobPhase.PausedByLimit } => _localizer[nameof(Strings.Library_Card_PausedLimitSoon)],
                    { Phase: GenerationJobPhase.PausedByPlayer } => _localizer[nameof(Strings.Library_Card_PausedPlayer)],
                    { Phase: GenerationJobPhase.Interrupted } => _localizer[nameof(Strings.Library_Card_Interrupted)],
                    { Phase: GenerationJobPhase.Failed } => _localizer[nameof(Strings.Library_Card_Failed)],
                    { Phase: GenerationJobPhase.WaitingForReview } => _localizer[nameof(Strings.Library_Card_Review)],
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

    /// <summary>Shows the operation running now (<paramref name="operation"/> when it is on this game) and whether any runs.</summary>
    public void ApplyOperation(InstallOperation? operation, bool busy)
    {
        var mine = operation is not null && operation.Game == Game.Id ? operation : null;
        if (mine == Operation && busy == IsInstallBusy)
        {
            return;
        }

        Operation = mine;
        IsInstallBusy = busy;
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
