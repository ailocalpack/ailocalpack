using System.Collections.ObjectModel;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings.GitHub;

/// <summary>
/// Settings → GitHub → My published packs (design §8; task 62): every local pack sent to the registry, newest first —
/// name, game · language · revision, status (<c>PR #… · CI running</c>, <c>PR #… · CI failed</c>, <c>PR #… merged ·
/// alpha</c>, <c>alpha · n/3 confirmed</c>, <c>verified</c>), 👍, and <c>Discussion ↗</c> / <c>PR ↗</c> / <c>Fix and
/// resubmit</c>. Read again each time the section opens and when a publish or a PR moved on. With none: where publishing
/// starts (signed in) or that signing in shows them.
/// </summary>
public sealed class PublishedPacksRow : SettingRow
{
    private readonly GitHubAccountRow _account;
    private readonly PublishFlow _flow;
    private readonly LocalPacks _localPacks;
    private readonly GameLibrary _library;
    private readonly IUiDispatcher _ui;
    private bool _shown;

    public PublishedPacksRow(SettingRowServices services, GitHubAccountRow account, PublishFlow flow, LocalPacks localPacks, GameLibrary library, IUiDispatcher ui)
        : base(services, nameof(Strings.Settings_PublishedPacks_Label), nameof(Strings.Settings_PublishedPacks_Keywords))
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        _flow = flow;
        _localPacks = localPacks;
        _library = library;
        _ui = ui;
        account.StateChanged += (_, _) => OnPropertyChanged(nameof(Text));
        flow.Changed += (_, _) => Queue();
        localPacks.Changed += (_, _) => Queue();
    }

    /// <summary>The packs sent to the registry from this machine.</summary>
    public ObservableCollection<PublishedPackItem> Packs { get; } = [];

    public bool HasPacks => Packs.Count > 0;

    public bool IsEmpty => Packs.Count == 0;

    /// <summary>The empty state's text.</summary>
    public string Text => Localizer[_account.IsSignedIn ? nameof(Strings.Settings_PublishedPacks_None) : nameof(Strings.Settings_PublishedPacks_SignIn)];

    public override string? Help => null;

    /// <summary>Reads the packs again (UI thread); from then on follows publishing.</summary>
    public void Refresh()
    {
        _shown = true;
        IReadOnlyList<LocalPack> packs;
        try
        {
            packs = _localPacks.ListAll();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            packs = [];
        }

        Packs.Clear();
        foreach (var pack in packs.Where(p => p.Report.Published is not null).OrderByDescending(p => p.Report.Published!.At))
        {
            Packs.Add(new PublishedPackItem(pack, _library.Catalog.Find(pack.Manifest.Game), _flow, Localizer));
        }

        OnPropertyChanged(nameof(HasPacks));
        OnPropertyChanged(nameof(IsEmpty));
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(Text));
        if (_shown)
        {
            Refresh();
        }
    }

    private void Queue()
    {
        if (_shown)
        {
            _ui.Post(Refresh);
        }
    }
}

/// <summary>One published pack of <see cref="PublishedPacksRow"/>.</summary>
public sealed class PublishedPackItem
{
    public PublishedPackItem(LocalPack pack, Game? game, PublishFlow flow, Localization.Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(localizer);
        var publication = pack.Report.Published!;
        var status = flow.StatusOf(pack);
        Pack = pack;
        Phase = status.Phase;
        Title = status.Live?.Title ?? pack.Manifest.Title;
        Meta = localizer.Format(
            nameof(Strings.Settings_PublishedPack_Meta),
            game?.Name ?? pack.Manifest.Game.ToString(),
            LanguageNames.Native(pack.Manifest.Language),
            status.Live?.Revision ?? publication.Revision);
        StatusText = status.Phase switch
        {
            PublishPhase.Published when status.Live!.Status == PackStatus.Verified => localizer[nameof(Strings.Library_Card_Verified)],
            PublishPhase.Published => localizer.Format(nameof(Strings.Settings_PublishedPack_Confirmed), Math.Min(status.Live!.Rating.Works, PublishStatuses.VerifiedConfirmations), PublishStatuses.VerifiedConfirmations),
            PublishPhase.CiFailed => localizer.Format(nameof(Strings.Publish_Row_CiFailed), publication.PullRequest),
            PublishPhase.Merged => localizer.Format(nameof(Strings.Publish_Row_Merged), publication.PullRequest),
            PublishPhase.Closed => localizer.Format(nameof(Strings.Publish_Row_Closed), publication.PullRequest),
            PublishPhase.Sending => localizer[nameof(Strings.Publish_Row_Sending)],
            PublishPhase.Failed => localizer[nameof(Strings.Publish_Row_Failed)],
            _ => localizer.Format(nameof(Strings.Publish_Row_Checking), publication.PullRequest),
        };
        Likes = status.Live?.Rating.Likes;
        LikesName = Likes is { } likes ? localizer.Format(nameof(Strings.Settings_PublishedPack_Likes), likes) : null;
        IsBad = status.Phase is PublishPhase.CiFailed or PublishPhase.Closed or PublishPhase.Failed;
        IsOk = status.Phase is PublishPhase.Published or PublishPhase.Merged;
        var discussion = status.Live?.Discussion;
        DiscussionCommand = discussion is null ? null : new RelayCommand(() => flow.Open(discussion));
        OpenPrCommand = discussion is null ? new RelayCommand(() => flow.Open(publication.PullRequestUrl)) : null;
        ResubmitCommand = game is not null && PublishStatuses.CanResubmit(status) ? new RelayCommand(() => flow.Resubmit(game, pack), () => !flow.IsBusy) : null;
    }

    public LocalPack Pack { get; }

    public PublishPhase Phase { get; }

    public string Title { get; }

    /// <summary>"Portal 2 · Русский · rev 2".</summary>
    public string Meta { get; }

    public string StatusText { get; }

    public bool IsBad { get; }

    public bool IsOk { get; }

    /// <summary>👍 once the index lists the pack.</summary>
    public int? Likes { get; }

    public string? LikesName { get; }

    public bool HasLikes => Likes is not null;

    public IRelayCommand? DiscussionCommand { get; }

    /// <summary><c>PR ↗</c> until the pack has a Discussion.</summary>
    public IRelayCommand? OpenPrCommand { get; }

    /// <summary><c>Fix and resubmit</c> while CI rejected the PR.</summary>
    public IRelayCommand? ResubmitCommand { get; }

    public bool HasDiscussion => DiscussionCommand is not null;

    public bool HasPr => OpenPrCommand is not null;

    public bool CanResubmit => ResubmitCommand is not null;
}
