using System.Collections.ObjectModel;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Pipeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>A line of the thread between rules: a draft built, the player played it (and how it went).</summary>
public sealed record FixSystemItem(string Text, bool IsBad) : FixItem;

/// <summary>The player's request (right, accent tint) with its attachment chips.</summary>
public sealed record FixPlayerItem(string Text, IReadOnlyList<FixAttachmentChip> Attachments) : FixItem
{
    public bool HasAttachments => Attachments.Count > 0;
}

/// <summary>One item of the conversation thread.</summary>
public abstract record FixItem;

/// <summary>A line of the agent's ✓ list.</summary>
public sealed record FixStepRow(string Text, bool IsRunning);

/// <summary>A before / after row of a change or a plan.</summary>
public sealed record FixSampleRow(string Key, string Before, string After);

/// <summary>A tile of the change card.</summary>
public sealed record FixTile(string Title, string Value, bool IsOk, bool IsBad);

/// <summary>An answer the agent offers to its question; a click sends it.</summary>
public sealed record FixChoice(string Text, IRelayCommand Command);

/// <summary>An attachment chip: kind icon, name, small detail; ✕ while it can still be removed (the composer's).</summary>
public sealed class FixAttachmentChip
{
    public FixAttachmentChip(FixAttachment attachment, string name, string detail, Action<FixAttachmentChip>? remove, string removeName = "")
    {
        Attachment = attachment;
        Name = name;
        Detail = detail;
        CanRemove = remove is not null;
        RemoveName = removeName;
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this), () => remove is not null);
    }

    public FixAttachment Attachment { get; }

    public string Name { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public bool IsBad => Attachment.Kind == FixAttachmentKind.Session;

    public string Icon => Attachment.Kind switch
    {
        FixAttachmentKind.Session => "IconXCircle",
        FixAttachmentKind.GameLog => "IconFile",
        FixAttachmentKind.Screenshot => "IconImage",
        FixAttachmentKind.Strings => "IconType",
        _ => "IconBox",
    };

    public bool CanRemove { get; }

    /// <summary>"Remove log.txt", the ✕ button's name for screen readers.</summary>
    public string RemoveName { get; }

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>The change card of an agent turn: a new draft.</summary>
public sealed record FixChangeCard(
    string Title,
    string Pill,
    bool PillIsNew,
    bool PillIsBad,
    bool PillIsOk,
    IReadOnlyList<FixTile> Tiles,
    IReadOnlyList<FixSampleRow> Samples,
    string InstallText,
    bool CanInstall,
    IRelayCommand InstallCommand,
    bool CanUndo,
    IRelayCommand UndoCommand)
{
    public bool HasSamples => Samples.Count > 0;

    public bool PillIsMuted => !PillIsNew && !PillIsBad && !PillIsOk;
}

/// <summary>The plan card of an agent turn: a large change that waits for the player.</summary>
public sealed record FixPlanCard(
    string Title,
    string Pill,
    bool IsWaiting,
    IReadOnlyList<string> Lines,
    IReadOnlyList<FixSampleRow> Samples,
    IRelayCommand ApplyCommand,
    IRelayCommand ChangeCommand)
{
    public bool HasSamples => Samples.Count > 0;
}

/// <summary>The agent's turn (left, teal bot): who, the steps, its words, its question's choices, a change, a plan, or why it failed.</summary>
public sealed record FixAgentItem(
    string Who,
    IReadOnlyList<FixStepRow> Steps,
    string Text,
    IReadOnlyList<FixChoice> Choices,
    FixChangeCard? Change,
    FixPlanCard? Plan,
    string Failure,
    string FailureDetail,
    bool IsWorking) : FixItem
{
    /// <summary>A finished turn shows its first 4 steps and folds the rest behind "More steps: N" (more than 5 in all).</summary>
    public const int ShownSteps = 4;

    public bool HasSteps => Steps.Count > 0;

    public bool FoldsSteps => !IsWorking && Steps.Count > ShownSteps + 1;

    public IReadOnlyList<FixStepRow> HeadSteps => FoldsSteps ? [.. Steps.Take(ShownSteps)] : Steps;

    public IReadOnlyList<FixStepRow> MoreSteps => FoldsSteps ? [.. Steps.Skip(ShownSteps)] : [];

    /// <summary>"More steps: 5".</summary>
    public string MoreStepsText { get; init; } = string.Empty;

    public bool HasText => Text.Length > 0;

    public bool HasChoices => Choices.Count > 0;

    public bool HasChange => Change is not null;

    public bool HasPlan => Plan is not null;

    public bool HasFailure => Failure.Length > 0;

    public bool HasFailureDetail => FailureDetail.Length > 0;
}

/// <summary>A pill of the card's head: one per draft (crashed red, works / installed green, newest teal).</summary>
public sealed record FixDraftPill(string Text, bool IsBad, bool IsOk, bool IsNew)
{
    public bool IsMuted => !IsBad && !IsOk && !IsNew;
}

/// <summary>A row of the Drafts menu.</summary>
public sealed record FixDraftRow(
    string Name,
    string Status,
    string Pill,
    bool IsBad,
    bool IsOk,
    bool IsCurrent,
    bool CanGoBack,
    IRelayCommand GoBackCommand,
    bool CanPublish,
    IRelayCommand PublishCommand)
{
    public bool IsMuted => !IsBad && !IsOk;
}

/// <summary>
/// The Generation tab's "Fix with AI" card (task 75, design §6e) under the result: the conversation with the job's agent
/// (system lines for the drafts built and played, the player's requests with their attachments, the agent's answers with
/// their steps, words, choices, change card or plan), the Drafts menu and the composer (request,
/// attachments, <c>Send</c> / <c>Stop</c>). After a crash with nothing asked since, the request is prefilled with the
/// crash and its session, log and draft changes attached. Reads <see cref="FixService"/> again on every change.
/// </summary>
public sealed partial class FixViewModel : ObservableObject
{
    private readonly Game _game;
    private readonly FixService _fixes;
    private readonly InstallFlow _install;
    private readonly LocalPacks _localPacks;
    private readonly Localizer _localizer;
    private readonly IFilePicker? _picker;
    private readonly Action<LocalPack> _publish;
    private readonly string _agentName;
    private FixView? _view;
    private string? _prefilledFor;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isDraftsOpen;

    public FixViewModel(Game game, FixService fixes, InstallFlow install, LocalPacks localPacks, Localizer localizer, IFilePicker? picker, Action<LocalPack> publish, string agentName)
    {
        _game = game;
        _fixes = fixes;
        _install = install;
        _localPacks = localPacks;
        _localizer = localizer;
        _picker = picker;
        _publish = publish;
        _agentName = agentName;
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        StopCommand = new RelayCommand(() => _fixes.Stop(_game.Id), () => IsRunning);
        AddScreenshotCommand = new AsyncRelayCommand(AddScreenshotAsync, () => !IsRunning && _picker is not null);
        AddGameLogCommand = new RelayCommand(AddGameLog, () => !IsRunning);
        ToggleDraftsCommand = new RelayCommand(() => IsDraftsOpen = !IsDraftsOpen);
        ShowEarlierCommand = new RelayCommand(() =>
        {
            _showEarlier = true;
            Fold();
            OnPropertyChanged(nameof(ShownItems));
            OnPropertyChanged(nameof(EarlierCount));
            OnPropertyChanged(nameof(HasEarlier));
            OnPropertyChanged(nameof(EarlierText));
        });
        RestoreCommand = new RelayCommand(() => _install.Uninstall(_game), () => !_install.IsBusy);
        Refresh();
    }

    public ObservableCollection<FixItem> Items { get; } = [];

    /// <summary>
    /// The thread as shown: a long one keeps its last <see cref="ShownRounds"/> requests (with the lines just before them)
    /// and folds the earlier items behind <c>Show earlier messages</c> until the player opens them.
    /// </summary>
    public IReadOnlyList<FixItem> ShownItems { get; private set; } = [];

    /// <summary>Requests kept open in a long thread.</summary>
    public const int ShownRounds = 2;

    /// <summary>Items folded away at the top of the thread.</summary>
    public int EarlierCount { get; private set; }

    public bool HasEarlier => EarlierCount > 0;

    /// <summary>"Show earlier messages (12)".</summary>
    public string EarlierText => _localizer.Format(nameof(Strings.Fix_Earlier), EarlierCount);

    public IRelayCommand ShowEarlierCommand { get; }

    private bool _showEarlier;

    private void Fold()
    {
        var start = _showEarlier ? 0 : FoldStart(Items);
        EarlierCount = start;
        ShownItems = [.. Items.Skip(start)];
    }

    /// <summary>Where the shown part starts: the lines before the <see cref="ShownRounds"/>-th request from the end; 0 when little would fold.</summary>
    public static int FoldStart(IReadOnlyList<FixItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var players = items.Select((item, i) => (item, i)).Where(x => x.item is FixPlayerItem).Select(x => x.i).ToList();
        if (players.Count <= ShownRounds)
        {
            return 0;
        }

        var start = players[^ShownRounds];
        while (start > 0 && items[start - 1] is FixSystemItem)
        {
            start--;
        }

        return start < 3 ? 0 : start;
    }

    public ObservableCollection<FixAttachmentChip> Attachments { get; } = [];

    public IReadOnlyList<FixDraftPill> Pills { get; private set; } = [];

    public IReadOnlyList<FixDraftRow> Drafts { get; private set; } = [];

    /// <summary>"Tell Claude Code what's wrong, in your own words. …"</summary>
    public string Intro => _localizer.Format(nameof(Strings.Fix_Intro), _agentName);

    public bool HasItems => Items.Count > 0;

    public bool IsRunning => _view?.IsRunning == true;

    public bool CanSend => !IsRunning && _view is not null && Text.Trim().Length is > 0 and <= FixRules.MaxRequestLength;

    public bool ShowSend => !IsRunning;

    /// <summary>"Claude Code · usually 2–5 % of a day's limit", or "Claude Code is working on it…".</summary>
    public string CostLine => _localizer.Format(IsRunning ? nameof(Strings.Fix_Working) : nameof(Strings.Fix_Cost), _agentName);

    /// <summary>The composer's hint: the first request, after a change, a question or a plan.</summary>
    public string Placeholder => _localizer[_view?.State.Turns.LastOrDefault() switch
    {
        { Plan.State: FixPlanState.Waiting } => nameof(Strings.Fix_Placeholder_Plan),
        { Choices.Count: > 0 } => nameof(Strings.Fix_Placeholder_Answer),
        null => nameof(Strings.Fix_Placeholder),
        _ => nameof(Strings.Fix_Placeholder_More),
    }];

    public bool HasDrafts => Drafts.Count > 1;

    public string ProblemText => _view?.ProblemKind switch
    {
        FixProblem.Busy => _localizer[nameof(Strings.Fix_Problem_Busy)],
        FixProblem.NoPack => _localizer[nameof(Strings.Fix_Problem_NoPack)],
        FixProblem.Disk => _localizer.Format(nameof(Strings.Fix_Problem_Disk), _view.Problem ?? string.Empty),
        FixProblem.Agent => _localizer.Format(nameof(Strings.Fix_Problem_Agent), _view.Problem ?? string.Empty),
        { } => _localizer.Format(nameof(Strings.Fix_Problem_Unknown), _view.Problem ?? string.Empty),
        _ => string.Empty,
    };

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>The installed draft made the game close right after it started (design §6e's red banner).</summary>
    public bool ShowCrashBanner => _view is { Pack.Report.Crash: not null } view && _localPacks.IsInstalled(view.Pack);

    /// <summary>"The game closed 4 s after it started with your translation (Draft 1) installed. Your saves are untouched."</summary>
    public string CrashBannerText => _view?.Pack.Report.Crash is { } crash
        ? _localizer.Format(nameof(Strings.Fix_Crash_Banner), Seconds(crash.Lasted.TotalSeconds), _view.Pack.Report.DraftNumber)
        : string.Empty;

    /// <summary><c>Restore original files</c>: uninstalls the translation (asks first).</summary>
    public IRelayCommand RestoreCommand { get; }

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IAsyncRelayCommand AddScreenshotCommand { get; }

    public IRelayCommand AddGameLogCommand { get; }

    public IRelayCommand ToggleDraftsCommand { get; }

    /// <summary>Reads the conversation again (on the UI thread).</summary>
    public void Refresh()
    {
        _view = _fixes.Of(_game.Id);
        Items.Clear();
        if (_view is { } view)
        {
            foreach (var item in Thread(view))
            {
                Items.Add(item);
            }

            Fold();
            Pills = BuildPills(view);
            Drafts = BuildDrafts(view);
            Prefill(view);
        }
        else
        {
            Fold();
            Pills = [];
            Drafts = [];
        }

        OnPropertyChanged(string.Empty);
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AddScreenshotCommand.NotifyCanExecuteChanged();
        AddGameLogCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>After a crash the player hasn't asked about yet: the request and its attachments, once per crash.</summary>
    private void Prefill(FixView view)
    {
        if (view.Pack.Report.Crash is not { } crash || view.IsRunning)
        {
            return;
        }

        var key = $"{view.Entry.Job.Id}:{view.Pack.Report.DraftNumber}:{crash.At.ToUnixTimeSeconds()}";
        if (_prefilledFor == key || view.State.Turns.Any(t => t.At >= crash.At))
        {
            return;
        }

        _prefilledFor = key;
        if (Text.Trim().Length == 0)
        {
            Text = _localizer[nameof(Strings.Fix_Chip_Crash_Text)];
        }

        Attachments.Clear();
        foreach (var attachment in _fixes.CrashAttachments(_game))
        {
            AddChip(attachment);
        }
    }

    private void AddChip(FixAttachment attachment)
    {
        if (Attachments.Any(a => a.Attachment.Kind == attachment.Kind && attachment.Kind != FixAttachmentKind.Screenshot))
        {
            return;
        }

        var (name, detail) = ChipTexts(attachment, _view?.Pack.Report.DraftNumber ?? 1);
        Attachments.Add(new FixAttachmentChip(attachment, name, detail, c => Attachments.Remove(c), _localizer.Format(nameof(Strings.Fix_Attach_Remove), name)));
    }

    private (string Name, string Detail) ChipTexts(FixAttachment attachment, int draft) => attachment.Kind switch
    {
        FixAttachmentKind.Session => (_localizer[nameof(Strings.Fix_Attach_Session)], attachment.ExitCode is { } code
            ? _localizer.Format(nameof(Strings.Fix_Attach_SessionExit), Seconds(attachment.Seconds), code)
            : _localizer.Format(nameof(Strings.Fix_Attach_SessionDetail), Seconds(attachment.Seconds))),
        FixAttachmentKind.GameLog => (attachment.Name, _localizer[nameof(Strings.Fix_Attach_LogDetail)]),
        FixAttachmentKind.DraftChanges => (_localizer.Format(nameof(Strings.Fix_Attach_Changes), draft), string.Empty),
        FixAttachmentKind.Screenshot => (_localizer[nameof(Strings.Fix_Attach_Screenshot)], attachment.Name),
        _ => (_localizer[nameof(Strings.Fix_Attach_Strings)], (attachment.Keys?.Count ?? 0).ToString(_localizer.Culture)),
    };

    private string Seconds(double? seconds) => Math.Max(1, Math.Round(seconds ?? 0)).ToString(_localizer.Culture);

    private async Task SendAsync()
    {
        var text = Text.Trim();
        var attachments = Attachments.Select(a => a.Attachment).ToList();
        if (attachments.All(a => a.Kind != FixAttachmentKind.DraftChanges))
        {
            attachments.Add(FixAttachment.Changes());
        }

        var chips = Attachments.ToList();
        Text = string.Empty;
        Attachments.Clear();
        if (!await _fixes.SendAsync(_game, text, attachments))
        {
            // Nothing was sent: the request stays for another try.
            Text = text;
            foreach (var chip in chips)
            {
                Attachments.Add(chip);
            }
        }
    }

    private void SendChoice(string choice)
    {
        if (IsRunning)
        {
            return;
        }

        _ = _fixes.SendAsync(_game, choice, [FixAttachment.Changes()]);
    }

    private async Task AddScreenshotAsync()
    {
        if (_picker is null || await _picker.PickImageAsync(_localizer[nameof(Strings.Fix_Attach_PickScreenshot)]) is not { } path)
        {
            return;
        }

        if (_fixes.AddScreenshot(_game.Id, path) is { } attachment)
        {
            AddChip(attachment);
        }
    }

    /// <summary>
    /// A paste into the request (Ctrl+V / ⌘V, the context menu): an image on the clipboard (a file copied in the file
    /// manager, a screenshot taken to the clipboard, an image copied in a browser) becomes a screenshot chip, no file to
    /// save first. False when the clipboard holds no image, so the view pastes its text as usual.
    /// </summary>
    public async Task<bool> PasteImageAsync()
    {
        if (_picker is null || IsRunning || await _picker.ReadClipboardImageAsync() is not { } image)
        {
            return false;
        }

        var attachment = image.Path is { } path
            ? _fixes.AddScreenshot(_game.Id, path)
            : _fixes.AddScreenshot(_game.Id, PastedName, image.Png ?? []);
        if (attachment is not null)
        {
            AddChip(attachment);
        }

        return true;
    }

    /// <summary>The file name a pasted image gets in the job's attachments (a number added when taken).</summary>
    public const string PastedName = "pasted.png";

    /// <summary>The screenshot button's tooltip: pick a file, or paste an image into the request.</summary>
    public string AddScreenshotTip => _localizer.Format(nameof(Strings.Fix_Attach_AddScreenshotTip), OperatingSystem.IsMacOS() ? "⌘V" : "Ctrl+V");

    private void AddGameLog()
    {
        var since = _view?.Pack.Report.Crash is { } crash ? crash.At - crash.Lasted : DateTimeOffset.UtcNow.AddHours(-6);
        if (_fixes.AddGameLog(_game, since) is { } attachment)
        {
            AddChip(attachment);
        }
        else
        {
            NoLogText = _localizer[nameof(Strings.Fix_Attach_NoLog)];
            OnPropertyChanged(nameof(NoLogText));
            OnPropertyChanged(nameof(HasNoLog));
        }
    }

    /// <summary>"No game log written since the last session was found." after <c>Game log</c> found none.</summary>
    public string NoLogText { get; private set; } = string.Empty;

    public bool HasNoLog => NoLogText.Length > 0;

    // ---------- Thread ----------

    private List<FixItem> Thread(FixView view)
    {
        var timed = new List<(DateTimeOffset At, int Order, FixItem Item)>();
        var first = view.Drafts.FirstOrDefault();
        if (first?.Report is { } firstReport)
        {
            timed.Add((first.Created, 0, new FixSystemItem(_localizer.Format(
                firstReport.TestRun.Outcome == TestRunOutcome.Passed ? nameof(Strings.Fix_System_Built_Passed) : nameof(Strings.Fix_System_Built_Skipped), first.Number), false)));
        }

        foreach (var draft in view.Drafts)
        {
            if (draft.Report?.Crash is { } crash)
            {
                timed.Add((crash.At, 1, new FixSystemItem(_localizer.Format(nameof(Strings.Fix_System_Crashed), draft.Number, Seconds(crash.Lasted.TotalSeconds)), true)));
            }
            else if (draft.Report?.Tried is { } tried)
            {
                timed.Add((tried, 1, new FixSystemItem(_localizer.Format(nameof(Strings.Fix_System_Played), draft.Number), false)));
            }
        }

        foreach (var turn in view.State.Turns)
        {
            timed.Add((turn.At, 2, turn.Kind == FixTurnKind.Player
                ? new FixPlayerItem(turn.Text, [.. turn.Attachments.Select(a => { var (n, d) = ChipTexts(a, DraftAt(view, turn.At)); return new FixAttachmentChip(a, n, d, null); })])
                : Agent(view, turn, working: false)));
        }

        var items = timed.OrderBy(t => t.At).ThenBy(t => t.Order).Select(t => t.Item).ToList();
        if (view.IsRunning)
        {
            var steps = view.Steps.Select((s, i) => new FixStepRow(StepText(s), i == view.Steps.Count - 1)).ToList();
            items.Add(new FixAgentItem(_agentName, steps, string.Empty, [], null, null, string.Empty, string.Empty, IsWorking: true));
        }

        return items;
    }

    /// <summary>The draft that was current at <paramref name="at"/>: the last one built by then.</summary>
    private static int DraftAt(FixView view, DateTimeOffset at) =>
        view.State.Drafts.Where(d => d.Created <= at).Select(d => d.Number).DefaultIfEmpty(view.State.Drafts.Count == 0 ? view.Pack.Report.DraftNumber : 1).Max();

    private FixAgentItem Agent(FixView view, FixTurn turn, bool working)
    {
        var who = turn.Model is { Length: > 0 } model ? $"{_agentName} · {model}" : _agentName;
        var steps = turn.Steps.Select(s => new FixStepRow(StepText(s), false)).ToList();
        var choices = turn == view.State.Turns.LastOrDefault() && !view.IsRunning
            ? turn.Choices.Select(c => new FixChoice(c, new RelayCommand(() => SendChoice(c)))).ToList()
            : [];
        var failure = turn.Outcome switch
        {
            FixTurnOutcome.Failed => _localizer[nameof(Strings.Fix_Failed)],
            FixTurnOutcome.Stopped => _localizer[nameof(Strings.Fix_Stopped)],
            _ => string.Empty,
        };
        return new FixAgentItem(who, steps, turn.Text, choices, Change(view, turn), Plan(view, turn), failure,
            turn.Outcome == FixTurnOutcome.Failed ? turn.Problem ?? string.Empty : string.Empty, working)
        {
            MoreStepsText = _localizer.Format(nameof(Strings.Fix_MoreSteps), Math.Max(0, steps.Count - FixAgentItem.ShownSteps)),
        };
    }

    private FixChangeCard? Change(FixView view, FixTurn turn)
    {
        if (turn.Change is not { } change)
        {
            return null;
        }

        var current = view.Pack.Report.DraftNumber == change.Draft;
        var installed = current && _localPacks.IsInstalled(view.Pack);
        var report = view.Drafts.FirstOrDefault(d => d.Number == change.Draft)?.Report;
        var pill = report?.Crash is not null ? nameof(Strings.Fix_Pill_Crashed)
            : installed ? nameof(Strings.Fix_Pill_Installed)
            : report?.Tried is not null ? nameof(Strings.Fix_Pill_Works)
            : nameof(Strings.Fix_Pill_NotTried);
        var tiles = new List<FixTile> { new(_localizer[nameof(Strings.Fix_Tile_Strings)], change.Strings.ToString("N0", _localizer.Culture), false, false) };
        if (change.Terms > 0)
        {
            tiles.Add(new(_localizer[nameof(Strings.Fix_Tile_Glossary)], change.Terms.ToString("N0", _localizer.Culture), false, false));
        }

        if (change.Scripts)
        {
            tiles.Add(new(_localizer[nameof(Strings.Fix_Tile_Applied)], _localizer[nameof(Strings.Fix_Tile_Scripts)], false, false));
        }

        tiles.Add(new(_localizer[nameof(Strings.Fix_Tile_TestRun)], _localizer[change.TestRun switch
        {
            TestRunOutcome.Passed => nameof(Strings.Fix_TestRun_Passed),
            TestRunOutcome.Crashed => nameof(Strings.Fix_TestRun_Crashed),
            _ => nameof(Strings.Fix_TestRun_Skipped),
        }], change.TestRun == TestRunOutcome.Passed, change.TestRun == TestRunOutcome.Crashed));
        var previous = view.Drafts.Where(d => d.Number < change.Draft).Select(d => (int?)d.Number).Max();
        var pack = view.Pack;
        return new FixChangeCard(
            _localizer.Format(nameof(Strings.Fix_Change_Title), change.Draft),
            _localizer[pill],
            pill == nameof(Strings.Fix_Pill_NotTried),
            pill == nameof(Strings.Fix_Pill_Crashed),
            pill is nameof(Strings.Fix_Pill_Installed) or nameof(Strings.Fix_Pill_Works),
            tiles,
            Samples(change.Samples),
            _localizer.Format(nameof(Strings.Fix_InstallPlay), change.Draft),
            current && !installed && !_install.IsBusy,
            new AsyncRelayCommand(() => _install.InstallLocalAsync(_game, pack, play: true)),
            current && previous is not null && !view.IsRunning,
            new RelayCommand(() => _fixes.GoBack(_game.Id, previous ?? 1)));
    }

    private FixPlanCard? Plan(FixView view, FixTurn turn)
    {
        if (turn.Plan is not { } plan)
        {
            return null;
        }

        var lines = new List<string> { _localizer.Format(nameof(Strings.Fix_Plan_Strings), plan.Strings.ToString("N0", _localizer.Culture)) };
        if (plan.Terms > 0)
        {
            lines.Add(_localizer.Format(nameof(Strings.Fix_Plan_Terms), plan.Terms.ToString("N0", _localizer.Culture)));
        }

        if (plan.Scripts)
        {
            lines.Add(_localizer[nameof(Strings.Fix_Plan_Scripts)]);
        }

        lines.Add(_localizer[nameof(Strings.Fix_Plan_Applying)]);
        var waiting = plan.State == FixPlanState.Waiting && !view.IsRunning;
        var id = turn.Id;
        return new FixPlanCard(
            _localizer.Format(nameof(Strings.Fix_Plan_Title), view.State.NextDraft),
            _localizer[plan.State switch
            {
                FixPlanState.Waiting => nameof(Strings.Fix_Pill_Waiting),
                FixPlanState.Applied => nameof(Strings.Fix_Pill_Applied),
                _ => nameof(Strings.Fix_Pill_Dropped),
            }],
            waiting,
            lines,
            Samples(plan.Samples),
            new AsyncRelayCommand(() => _fixes.ApplyPlanAsync(_game, id)),
            new RelayCommand(() => _fixes.DropPlan(_game, id)));
    }

    private List<FixSampleRow> Samples(IReadOnlyList<FixStringChange> samples) =>
        [.. samples.Select(s => new FixSampleRow(
            s.Key,
            s.Before.Length == 0 ? _localizer[nameof(Strings.Fix_Samples_None)] : s.Before,
            s.After.Length == 0 ? _localizer[nameof(Strings.Fix_Samples_None)] : s.After))];

    /// <summary>The ✓ line of a step code (<see cref="FixSteps"/>).</summary>
    internal string StepText(string step) => step switch
    {
        "search" or "get_strings" => _localizer[nameof(Strings.Fix_Step_Search)],
        "put_translations" => _localizer[nameof(Strings.Fix_Step_Store)],
        "get_glossary" => _localizer[nameof(Strings.Fix_Step_ReadGlossary)],
        "put_glossary" => _localizer[nameof(Strings.Fix_Step_Glossary)],
        "validate_strings" => _localizer[nameof(Strings.Fix_Step_Validate)],
        "read_file_snippet" or "list_files" or "detect_engine" or "extract_strings" => _localizer[nameof(Strings.Fix_Step_ReadGame)],
        "Bash" or "shell" or "exec_command" or "command_execution" => _localizer[nameof(Strings.Fix_Step_Command)],
        "Edit" or "Write" or "MultiEdit" or "apply_patch" or "file_change" => _localizer[nameof(Strings.Fix_Step_Edit)],
        "Read" or "Glob" or "Grep" or "LS" => _localizer[nameof(Strings.Fix_Step_ReadFile)],
        FixSteps.Build => _localizer[nameof(Strings.Fix_Step_Build)],
        FixSteps.Saved => _localizer[nameof(Strings.Fix_Step_Saved)],
        _ => _localizer.Format(nameof(Strings.Fix_Step_Tool), step),
    };

    // ---------- Drafts ----------

    private List<FixDraftPill> BuildPills(FixView view) =>
        view.Drafts.Count <= 1 && view.State.Turns.Count == 0
            ? []
            : [.. view.Drafts.Select(d =>
            {
                var installed = d.IsCurrent && _localPacks.IsInstalled(view.Pack);
                var (key, bad, ok, isNew) = d.Report?.Crash is not null ? (nameof(Strings.Fix_Pill_Crashed), true, false, false)
                    : installed ? (nameof(Strings.Fix_Pill_Installed), false, true, false)
                    : d.Report?.Tried is not null ? (nameof(Strings.Fix_Pill_Works), false, true, false)
                    : d.IsCurrent ? (nameof(Strings.Fix_Pill_New), false, false, true)
                    : (nameof(Strings.Fix_Pill_NotTried), false, false, false);
                return new FixDraftPill($"{d.Number.ToString(_localizer.Culture)} · {_localizer[key]}", bad, ok, isNew);
            })];

    private List<FixDraftRow> BuildDrafts(FixView view) =>
        [.. view.Drafts.Select(d =>
        {
            var installed = d.IsCurrent && _localPacks.IsInstalled(view.Pack);
            var status = d.Report?.Crash is { } crash ? _localizer.Format(nameof(Strings.Fix_Draft_StatusCrashed), Seconds(crash.Lasted.TotalSeconds))
                : d.Report?.Tried is not null ? _localizer[nameof(Strings.Fix_Draft_StatusTried)]
                : _localizer[nameof(Strings.Fix_Draft_StatusNotTried)];
            var (pill, bad, ok) = d.Report?.Crash is not null ? (nameof(Strings.Fix_Pill_Crashed), true, false)
                : installed ? (nameof(Strings.Fix_Pill_Installed), false, true)
                : d.Report?.Tried is not null ? (nameof(Strings.Fix_Pill_Works), false, true)
                : (nameof(Strings.Fix_Pill_NotTried), false, false);
            var number = d.Number;
            var pack = view.Pack;
            return new FixDraftRow(
                _localizer.Format(nameof(Strings.Fix_Draft_Name), number),
                status,
                _localizer[pill],
                bad,
                ok,
                d.IsCurrent,
                !d.IsCurrent && !view.IsRunning,
                new RelayCommand(() => { IsDraftsOpen = false; _fixes.GoBack(_game.Id, number); }),
                d.IsCurrent && d.Report is { Tried: not null, Crash: null },
                new RelayCommand(() => { IsDraftsOpen = false; _publish(pack); }));
        })];
}
