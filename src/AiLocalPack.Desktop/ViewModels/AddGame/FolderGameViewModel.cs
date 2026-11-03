using System.ComponentModel;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.AddGame;

/// <summary>Where a game of a folder with several games stands in the one-at-a-time review.</summary>
public enum ReviewState
{
    /// <summary>Not reviewed yet (or reviewed and left without a choice).</summary>
    Pending,

    Added,

    /// <summary>Skipped by the player.</summary>
    Skipped,
}

/// <summary>What a game of a folder with several games is, before the review (overview tally, cover strip).</summary>
public enum FolderGameKind
{
    /// <summary>Not in the library yet: reviewed and can be added.</summary>
    New,

    /// <summary>New, with an anti-cheat: added as unavailable.</summary>
    AntiCheat,

    /// <summary>Already in the library: skipped automatically.</summary>
    InLibrary,

    /// <summary>Its files can't be read, so it has no fingerprint: skipped automatically.</summary>
    Unreadable,
}

/// <summary>
/// One game of a folder with several games (design §2): its cover and status in the overview strip, its segment in the
/// review's progress bar, its line in the result. Reviewable games carry a <see cref="Summary"/>.
/// </summary>
public sealed partial class FolderGameViewModel : ObservableObject
{
    private readonly Localizer _localizer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdded), nameof(IsSkipped), nameof(IsDone), nameof(StatusText), nameof(ResultText))]
    private ReviewState _state;

    /// <summary>The game shown in the review now (its progress segment is highlighted).</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public FolderGameViewModel(FoundGame game, GameSummaryViewModel? summary, string? error, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(game);
        Game = game;
        Summary = summary;
        Error = error;
        _localizer = localizer;
        if (summary is not null)
        {
            summary.PropertyChanged += OnSummaryChanged;
        }
    }

    public FoundGame Game { get; }

    /// <summary>The analyzed game, or <c>null</c> when its files can't be read (<see cref="Error"/>).</summary>
    public GameSummaryViewModel? Summary { get; }

    public string? Error { get; }

    /// <summary>Position in the review order (1-based), 0 for a game skipped automatically.</summary>
    public int Number { get; set; }

    public bool HasNumber => Number > 0;

    public string Name => Summary?.Name ?? Game.Name;

    public string RelativePath => Game.RelativePath.Length > 0 ? AddGameText.NativePath(Game.RelativePath) : ".";

    public string EngineText => Game.Engine.Kind.Engine == GameEngine.Unknown
        ? _localizer[nameof(Strings.AddGame_EngineUnknown)]
        : Game.Engine.Kind.DisplayName;

    /// <summary>The kind the game had when the folder was analyzed (a game added meanwhile stays <see cref="FolderGameKind.New"/>).</summary>
    public FolderGameKind Kind { get; init; }

    /// <summary>Reviewed one at a time (not in the library, readable).</summary>
    public bool IsReviewable => Kind is FolderGameKind.New or FolderGameKind.AntiCheat;

    public bool IsAdded => State == ReviewState.Added;

    public bool IsSkipped => State == ReviewState.Skipped;

    /// <summary>Added or skipped: the progress segment is filled.</summary>
    public bool IsDone => State != ReviewState.Pending;

    public bool IsBlocked => Kind == FolderGameKind.AntiCheat;

    /// <summary>Badge under the cover in the overview strip.</summary>
    public string StatusText => _localizer[
        IsAdded ? nameof(Strings.AddGame_Status_Added)
        : IsSkipped ? nameof(Strings.AddGame_Status_Skipped)
        : Kind switch
        {
            FolderGameKind.AntiCheat => nameof(Strings.AddGame_Status_AntiCheat),
            FolderGameKind.InLibrary => nameof(Strings.AddGame_Status_InLibrary),
            FolderGameKind.Unreadable => nameof(Strings.AddGame_Status_Unreadable),
            _ => nameof(Strings.AddGame_Status_New),
        }];

    /// <summary>Line of the result: "added", "added as unavailable (anti-cheat)", "skipped", "already in the library"…</summary>
    public string ResultText => _localizer[
        IsAdded ? (Kind == FolderGameKind.AntiCheat ? nameof(Strings.AddGame_Result_AddedBlocked) : nameof(Strings.AddGame_Result_Added))
        : Kind switch
        {
            FolderGameKind.InLibrary => nameof(Strings.AddGame_Result_InLibrary),
            FolderGameKind.Unreadable => nameof(Strings.AddGame_Result_Unreadable),
            _ when Summary is { IsDuplicate: true } => nameof(Strings.AddGame_Result_InLibrary),
            _ => nameof(Strings.AddGame_Result_Skipped),
        }];

    public Bitmap? CoverArt => Summary?.CoverArt;

    public Bitmap? CoverIcon => Summary?.CoverIcon;

    public bool HasCoverArt => CoverArt is not null;

    public bool HasCoverIcon => CoverIcon is not null;

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ResultText));
    }

    private void OnSummaryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GameSummaryViewModel.CoverImage):
                OnPropertyChanged(nameof(CoverArt));
                OnPropertyChanged(nameof(CoverIcon));
                OnPropertyChanged(nameof(HasCoverArt));
                OnPropertyChanged(nameof(HasCoverIcon));
                break;
            case nameof(GameSummaryViewModel.Name):
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(GameSummaryViewModel.Existing):
                OnPropertyChanged(nameof(ResultText));
                break;
        }
    }
}

/// <summary>A folder the scan left out, with its reason in words ("Tools/7-Zip — a utility, not a game").</summary>
public sealed record SkippedFolderViewModel(string Path, string Reason);

/// <summary>A game streaming in while a folder is searched: name, engine, relative path.</summary>
public sealed record ScannedGameViewModel(string Name, string Engine, string Path);
