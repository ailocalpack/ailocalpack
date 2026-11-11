using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>One mini-step of the sending strip: its label and whether it is done, running or still to come.</summary>
public sealed record PublishMiniStep(string Text, bool IsDone, bool IsCurrent)
{
    public string Icon => IsDone ? "IconCheck" : IsCurrent ? "IconLoader" : "IconCircle";
}

/// <summary>
/// A local pack's publish state as the result strip and the Packs → Mine details show it (task 61, design §7):
/// <i>Sending</i> (teal, "Publishing rev 4 of «Неон»…" with the mini-steps), <i>CI running</i> (teal, "PR #214 opened ·
/// CI is checking the pack", <c>Open PR</c>), <i>Failed</i> (red, the reason, <c>Publish again</c>), <i>Published</i>
/// (green, "Published: … · alpha", the 0/3 confirmations meter, <c>Copy link</c>, <c>Discussion</c>); task 62: <i>CI
/// failed</i> (red, "CI rejected PR #214", the reason and what the app fixes, <c>Open PR</c>, <c>Fix and resubmit</c>),
/// <i>Merged</i> (green, until the index lists it), <i>Closed</i> (red, <c>Publish again</c>). Not shown while the
/// pack was never sent (the "Looks good?" strip and <c>Publish</c> are). The state comes from <see cref="PublishFlow"/>;
/// the owner calls <see cref="Update"/> when the pack, the flow or the index changed.
/// </summary>
public sealed partial class PackPublishViewModel : ObservableObject
{
    private readonly PublishFlow _flow;
    private readonly Func<Game> _game;
    private readonly Localizer _localizer;

    public PackPublishViewModel(PublishFlow flow, Func<Game> game, LocalPack pack, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(pack);
        _flow = flow;
        _game = game;
        _localizer = localizer;
        Pack = pack;
        Status = flow.StatusOf(pack);
        PublishCommand = new RelayCommand(() => _flow.Publish(_game(), Pack), () => CanPublish);
        ResubmitCommand = new RelayCommand(() => _flow.Resubmit(_game(), Pack), () => CanResubmit);
        OpenPrCommand = new RelayCommand(() => _flow.Open(Status.Publication!.PullRequestUrl), () => Status.Publication is not null);
        DiscussionCommand = new RelayCommand(() => _flow.Open(Status.Live!.Discussion!), () => HasDiscussion);
        CopyLinkCommand = new AsyncRelayCommand(() => _flow.CopyLinkAsync(Link!), () => Link is not null);
    }

    public LocalPack Pack { get; private set; }

    public PublishStatus Status { get; private set; }

    public PublishPhase Phase => Status.Phase;

    /// <summary>The status strip is shown: the pack was sent, is being sent, or the last try failed.</summary>
    public bool ShowStatus => Phase != PublishPhase.NotPublished;

    public bool IsSending => Phase == PublishPhase.Sending;

    public bool IsChecking => Phase == PublishPhase.Checking;

    public bool IsFailed => Phase == PublishPhase.Failed;

    public bool IsPublished => Phase == PublishPhase.Published;

    public bool IsCiFailed => Phase == PublishPhase.CiFailed;

    public bool IsMerged => Phase == PublishPhase.Merged;

    public bool IsClosed => Phase == PublishPhase.Closed;

    /// <summary>Red: the last try failed, CI rejected the PR, or it was closed.</summary>
    public bool IsBad => IsFailed || IsCiFailed || IsClosed;

    /// <summary>Green: merged or published.</summary>
    public bool IsOk => IsPublished || IsMerged;

    /// <summary>Teal while sending or CI runs (the strip's <c>gen</c> look).</summary>
    public bool IsTeal => IsSending || IsChecking;

    /// <summary><c>Publish</c> / <c>Publish again</c>: tried in game, never sent or the last try failed, nothing else being sent.</summary>
    public bool CanPublish => PublishStatuses.CanPublish(Pack, Status) && !_flow.IsBusy;

    /// <summary><c>Publish</c> is shown (it may be off while another pack is sent).</summary>
    public bool ShowPublish => PublishStatuses.CanPublish(Pack, Status);

    public string PublishText => _localizer[IsFailed || IsClosed ? nameof(Strings.Publish_Again) : nameof(Strings.Gen_Result_Publish)];

    /// <summary><c>Publish again</c> in the strip: the last try failed, or the PR was closed.</summary>
    public bool ShowPublishAgain => (IsFailed || IsClosed) && ShowPublish;

    /// <summary><c>Fix and resubmit</c> is shown: CI rejected the PR (task 62).</summary>
    public bool ShowResubmit => PublishStatuses.CanResubmit(Status);

    /// <summary><c>Fix and resubmit</c> can be pressed: nothing else is being sent.</summary>
    public bool CanResubmit => ShowResubmit && !_flow.IsBusy;

    public string Icon => Phase switch
    {
        PublishPhase.Sending => "IconLoader",
        PublishPhase.Checking => "IconPullRequest",
        PublishPhase.Failed or PublishPhase.CiFailed or PublishPhase.Closed => "IconXCircle",
        PublishPhase.Published or PublishPhase.Merged => "IconCheckCircle",
        _ => "IconUpload",
    };

    /// <summary>The strip's bold line.</summary>
    public string Title => Phase switch
    {
        PublishPhase.Sending => _localizer.Format(nameof(Strings.Publish_Sending_Title), PublishTexts.Target(Status.Run!.TargetTitle, Status.Run.TargetRevision, _localizer)),
        PublishPhase.Checking => _localizer.Format(nameof(Strings.Publish_Checking_Title), Status.Publication!.PullRequest),
        PublishPhase.Failed => _localizer.Format(nameof(Strings.Publish_Failed_Title), Pack.Manifest.Title),
        PublishPhase.Published => _localizer.Format(nameof(Strings.Publish_Published_Title), PublishedTarget, StatusName),
        PublishPhase.CiFailed => _localizer.Format(nameof(Strings.Publish_CiFailed_Title), Status.Publication!.PullRequest),
        PublishPhase.Merged => _localizer.Format(nameof(Strings.Publish_Merged_Title), Status.Publication!.PullRequest, PublishedTarget),
        PublishPhase.Closed => _localizer.Format(nameof(Strings.Publish_Closed_Title), Status.Publication!.PullRequest),
        _ => string.Empty,
    };

    /// <summary>The strip's second line (what happens now, the failure's reason, how a pack becomes verified).</summary>
    public string Text => Phase switch
    {
        PublishPhase.Checking => _localizer[nameof(Strings.Publish_Checking_Text)],
        PublishPhase.Failed => PublishTexts.Failure(Status.Failure!, _flow.Registry, _localizer),
        PublishPhase.Published when Status.Live!.Status == PackStatus.Verified => _localizer[nameof(Strings.Publish_Published_VerifiedText)],
        PublishPhase.Published => _localizer.Format(nameof(Strings.Publish_Published_Text), PublishStatuses.VerifiedConfirmations),
        PublishPhase.CiFailed => PublishTexts.CiFailure(Status.CiFailure!, _localizer),
        PublishPhase.Merged => _localizer[nameof(Strings.Publish_Merged_Text)],
        PublishPhase.Closed => _localizer[nameof(Strings.Publish_Closed_Text)],
        _ => string.Empty,
    };

    public bool HasText => Text.Length > 0;

    /// <summary>Fork the registry → Commit the pack → Open a pull request, while sending.</summary>
    public IReadOnlyList<PublishMiniStep> Steps
    {
        get
        {
            if (!IsSending)
            {
                return [];
            }

            var current = Status.Run!.Step;
            return
            [
                .. Enum.GetValues<PublishStep>().Select(step => new PublishMiniStep(
                    PublishTexts.Step(step, _localizer),
                    current is { } c && step < c,
                    current is { } d ? step == d : step == PublishStep.Fork)),
            ];
        }
    }

    // ---------- Published ----------

    /// <summary>The confirmations meter (published as alpha): one box per needed confirmation.</summary>
    public bool ShowMeter => IsPublished && Status.Live!.Status != PackStatus.Verified;

    /// <summary>"works" confirmations so far, at most the number needed.</summary>
    public int Confirmations => Status.Live is { } live ? Math.Clamp(live.Rating.Works, 0, PublishStatuses.VerifiedConfirmations) : 0;

    public IReadOnlyList<bool> MeterBoxes => [.. Enumerable.Range(0, PublishStatuses.VerifiedConfirmations).Select(i => i < Confirmations)];

    /// <summary>"0 of 3 players confirmed “works”".</summary>
    public string MeterText => _localizer.Format(nameof(Strings.Publish_Meter), Confirmations, PublishStatuses.VerifiedConfirmations);

    public bool HasDiscussion => IsPublished && Status.Live?.Discussion is not null;

    /// <summary>What <c>Copy link</c> copies: the pack's Discussion, else its PR.</summary>
    public Uri? Link => IsPublished ? Status.Live?.Discussion ?? Status.Publication?.PullRequestUrl : null;

    /// <summary><c>Open PR</c> while CI runs, after CI rejected it, once merged or closed, or after a failure that left a PR.</summary>
    public bool ShowOpenPr => (IsChecking || IsFailed || IsCiFailed || IsMerged || IsClosed) && Status.Publication is not null;

    public bool ShowCopyLink => Link is not null;

    // ---------- Mine row ----------

    /// <summary>The Mine row's meta line, or <c>null</c> to keep "local · not published".</summary>
    public string? RowMeta => Phase switch
    {
        PublishPhase.Sending => _localizer[nameof(Strings.Publish_Row_Sending)],
        PublishPhase.Checking => _localizer.Format(nameof(Strings.Publish_Row_Checking), Status.Publication!.PullRequest),
        PublishPhase.Published => _localizer.Format(nameof(Strings.Publish_Row_Published), StatusName, Status.Live!.Revision),
        PublishPhase.Failed => _localizer[nameof(Strings.Publish_Row_Failed)],
        PublishPhase.CiFailed => _localizer.Format(nameof(Strings.Publish_Row_CiFailed), Status.Publication!.PullRequest),
        PublishPhase.Merged => _localizer.Format(nameof(Strings.Publish_Row_Merged), Status.Publication!.PullRequest),
        PublishPhase.Closed => _localizer.Format(nameof(Strings.Publish_Row_Closed), Status.Publication!.PullRequest),
        _ => null,
    };

    public IRelayCommand PublishCommand { get; }

    public IRelayCommand ResubmitCommand { get; }

    public IRelayCommand OpenPrCommand { get; }

    public IRelayCommand DiscussionCommand { get; }

    public IAsyncRelayCommand CopyLinkCommand { get; }

    private string StatusName => _localizer[Status.Live?.Status == PackStatus.Verified ? nameof(Strings.Library_Card_Verified) : nameof(Strings.Library_Card_Alpha)];

    /// <summary>"rev 4 of «Неон»" (a revision after the first), "«Курьер 2087»".</summary>
    private string PublishedTarget => Status.Live is { } live
        ? PublishTexts.Target(live.Title, live.Revision > 1 ? live.Revision : null, _localizer)
        : PublishTexts.Target(Pack.Manifest.Title, null, _localizer);

    /// <summary>The pack was read again, or the flow or the index moved on.</summary>
    public void Update(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        Status = _flow.StatusOf(pack);
        RefreshTexts();
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        PublishCommand.NotifyCanExecuteChanged();
        ResubmitCommand.NotifyCanExecuteChanged();
        OpenPrCommand.NotifyCanExecuteChanged();
        DiscussionCommand.NotifyCanExecuteChanged();
        CopyLinkCommand.NotifyCanExecuteChanged();
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Pack.Manifest.Id}: {Phase}");
}
