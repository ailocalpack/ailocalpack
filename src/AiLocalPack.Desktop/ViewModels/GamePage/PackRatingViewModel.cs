using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Ratings;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>
/// "Your rating" under the installed registry pack in the Packs tab's details (design §4; task 63): <c>👍 Like</c> (a
/// toggle: <c>Liked</c> takes it back), <c>Works</c> / <c>Doesn't work</c> with an optional comment, and a line saying
/// what was reported or why a send failed. Shown only after the player played with the installed revision
/// (<see cref="RatingRules.ShowBlock"/>); for an author of the pack the confirmation buttons are replaced by a note;
/// without a Discussion yet nothing can be sent and a note says so. Everything goes through <see cref="PackRatings"/>
/// (sign-in on first use).
/// </summary>
public sealed partial class PackRatingViewModel : ObservableObject
{
    private readonly PackRatings _ratings;
    private readonly Localizer _localizer;
    private readonly Func<Game> _game;
    private RegistryPack _pack;
    private InstalledPackState? _installed;
    private IReadOnlyList<PackAuthor>? _authors;

    [ObservableProperty]
    private string _comment = string.Empty;

    public PackRatingViewModel(RegistryPack pack, InstalledPackState? installed, PackRatings ratings, Localizer localizer, Func<Game> game)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _pack = pack;
        _installed = installed;
        _ratings = ratings;
        _localizer = localizer;
        _game = game;
        LikeCommand = new RelayCommand(Like, () => CanSend);
        WorksCommand = new RelayCommand(() => Report(PackVerdict.Works), () => CanSend);
        DoesNotWorkCommand = new RelayCommand(() => Report(PackVerdict.DoesNotWork), () => CanSend);
    }

    public IRelayCommand LikeCommand { get; }

    public IRelayCommand WorksCommand { get; }

    public IRelayCommand DoesNotWorkCommand { get; }

    private GameId GameId => _game().Id;

    private PackRating? Rating => _ratings.Find(GameId, _pack.Language, _pack.Id);

    private RatingSend? Send => _ratings.SendOf(GameId, _pack.Language, _pack.Id);

    /// <summary>The installed revision (the one played and reported).</summary>
    private int Revision => _installed?.Revision ?? _pack.Revision;

    /// <summary>The block is shown: the installed pack, played with since it was installed.</summary>
    public bool IsVisible => _installed?.PackId == _pack.Id && RatingRules.ShowBlock(_installed, Rating);

    public bool HasDiscussion => DiscussionRef.TryParse(_pack.Discussion, out _);

    /// <summary>A like or report is on its way.</summary>
    public bool IsSending => Send is { IsRunning: true };

    public bool CanSend => HasDiscussion && !IsSending;

    public bool IsLiked => Rating?.Liked ?? false;

    /// <summary>"Like" / "Liked".</summary>
    public string LikeText => _localizer[IsLiked ? nameof(Strings.Rating_Liked) : nameof(Strings.Rating_Like)];

    /// <summary>The signed-in player is credited on the pack: they can't confirm it.</summary>
    public bool IsAuthor => RatingRules.IsAuthor(_ratings.Login, _pack.Author, _authors);

    public bool ShowConfirm => !IsAuthor;

    public bool IsWorksReported => Rating?.Reported == PackVerdict.Works;

    public bool IsDoesNotWorkReported => Rating?.Reported == PackVerdict.DoesNotWork;

    public bool IsFailed => Send is { Error: not null };

    /// <summary>
    /// "Sending…", the reason a send failed, "You reported «Works» for rev 3 on 12.11.2026. …", or what a report
    /// carries; for an author why there are no confirmation buttons; without a Discussion that rating opens later.
    /// </summary>
    public string StatusText => this switch
    {
        { HasDiscussion: false } => _localizer[nameof(Strings.Rating_NoDiscussion)],
        { IsSending: true } => _localizer[nameof(Strings.Rating_Sending)],
        { Send.Error: { } error } => RatingTexts.Error(error, _localizer),
        { IsAuthor: true } => _localizer[nameof(Strings.Rating_Author)],
        { Rating: { Reported: { } verdict, ReportedAt: { } at } } => _localizer.Format(
            nameof(Strings.Rating_Reported),
            _localizer[verdict == PackVerdict.Works ? nameof(Strings.Rating_Works) : nameof(Strings.Rating_DoesNotWork)],
            Revision,
            at.ToLocalTime().ToString("d", _localizer.Culture)),
        _ => _localizer[nameof(Strings.Rating_Hint)],
    };

    /// <summary>The comment box: only where Works / Doesn't work can be sent.</summary>
    public bool ShowComment => ShowConfirm && HasDiscussion;

    /// <summary>The index entry, the installed pack or the manifest's authors changed.</summary>
    internal void Update(RegistryPack pack, InstalledPackState? installed, IReadOnlyList<PackAuthor>? authors)
    {
        _pack = pack;
        _installed = installed;
        _authors = authors;
        Refresh();
    }

    /// <summary>The marks or the send changed, or the UI language.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(string.Empty);
        LikeCommand.NotifyCanExecuteChanged();
        WorksCommand.NotifyCanExecuteChanged();
        DoesNotWorkCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the player's like and report from GitHub once (signed in only).</summary>
    internal Task SyncAsync() => IsVisible ? _ratings.SyncAsync(GameId, _pack, Revision) : Task.CompletedTask;

    private void Like() => _ratings.Like(_game(), _pack, Revision, !IsLiked);

    private void Report(PackVerdict verdict)
    {
        if (IsAuthor)
        {
            return;
        }

        _ratings.Report(_game(), _pack, Revision, verdict, Comment);
    }
}
