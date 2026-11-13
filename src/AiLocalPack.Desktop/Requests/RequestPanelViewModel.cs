using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Requests;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Ratings;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GamePage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Requests;

/// <summary>One step of a request's timeline: Requested → Translator found → Translating → Published.</summary>
public sealed class RequestStepViewModel(string name, string detail, bool isDone, bool isCurrent, string icon, bool isLast)
{
    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public bool IsDone { get; } = isDone;

    public bool IsCurrent { get; } = isCurrent;

    public bool IsReached => IsDone || IsCurrent;

    /// <summary>Resource key of the icon (a check once done).</summary>
    public string Icon { get; } = isDone ? "IconCheck" : icon;

    /// <summary>The line to the next step (none after the last); green once this step is done.</summary>
    public bool HasLine { get; } = !isLast;
}

/// <summary>A player's note on the request: who and what.</summary>
public sealed record RequestNoteViewModel(string Author, string Text);

/// <summary>
/// The Text tab's translation request (task 74; design: requests, variant 1) for the game and the tab's language, shown
/// while the language has no pack: the empty state asks for it (<c>Request translation</c>, or <c>Me too · 12</c> when
/// others did), the player's own request is a card (timeline, who is translating, notes, <c>Open on GitHub</c>,
/// <c>Withdraw my request</c>); once a pack exists for the player's request, a green strip says it is done until
/// dismissed. <see cref="Refresh"/> follows the page (any change of the request, the pack list or the language).
/// </summary>
public sealed partial class RequestPanelViewModel : ObservableObject
{
    private const int NotesShown = 3;

    private readonly TranslationRequests _requests;
    private readonly Localizer _localizer;
    private readonly ISystemShell _shell;
    private readonly Action _openGeneration;
    private Game _game;
    private string _language;
    private bool _hasPacks;

    public RequestPanelViewModel(TranslationRequests requests, Game game, string language, Localizer localizer, ISystemShell shell, Action openGeneration)
    {
        _requests = requests;
        _game = game;
        _language = language;
        _localizer = localizer;
        _shell = shell;
        _openGeneration = openGeneration;
        View = requests.ViewOf(game.Id, language);
    }

    public RequestView View { get; private set; }

    /// <summary>The player asked (or voted): the card replaces the empty state.</summary>
    public bool IsMine => View.Mine;

    public string LanguageName => LanguageNames.Native(_language);

    // ---------- Empty state ----------

    /// <summary>"Nobody has made one yet. Ask the community…" / "12 players asked for a translation into Русский…".</summary>
    public string AskText => View.Votes > 0
        ? _localizer.Plural(nameof(Strings.Request_Empty_Others_One), View.Votes, LanguageName)
        : _localizer[nameof(Strings.Request_Empty_Text)];

    /// <summary>"Request translation" / "Me too · 12".</summary>
    public string AskButtonText => View.Votes > 0
        ? _localizer.Format(nameof(Strings.Request_MeToo), View.Votes.ToString("N0", _localizer.Culture))
        : _localizer[nameof(Strings.Request_Ask)];

    public bool HasDiscussion => View.Discussion is not null;

    // ---------- Sending ----------

    public bool IsSending => _requests.SendOf(_game.Id, _language) is { IsRunning: true };

    public bool CanAct => !IsSending;

    /// <summary>"Sending to GitHub…" or why the last ask / withdraw failed.</summary>
    public string StatusText => _requests.SendOf(_game.Id, _language) switch
    {
        { IsRunning: true } => _localizer[nameof(Strings.Rating_Sending)],
        { Error: { } error } => RatingTexts.Error(error, _localizer),
        _ => string.Empty,
    };

    public bool HasStatus => StatusText.Length > 0;

    public bool IsFailed => _requests.SendOf(_game.Id, _language) is { Error: not null };

    // ---------- The player's request card ----------

    /// <summary>"Requested: Русский".</summary>
    public string CardTitle => _localizer.Format(nameof(Strings.Request_Card_Title), LanguageName);

    /// <summary>"Asked 13.11.2026 · you and 12 other players" / "Asked 13.11.2026 · only you so far".</summary>
    public string CardSubtitle
    {
        get
        {
            var since = View.Since is { } at ? at.ToLocalTime().ToString("d", _localizer.Culture) : "—";
            return View.Others > 0
                ? _localizer.Plural(nameof(Strings.Request_Card_Sub_One), View.Others, since)
                : _localizer.Format(nameof(Strings.Request_Card_SubAlone), since);
        }
    }

    public IReadOnlyList<RequestStepViewModel> Steps
    {
        get
        {
            var claim = View.Claim;
            var reached = claim switch
            {
                null => 0,
                { State: RequestClaimState.Published } => 3,
                _ => 2,
            };
            string Name(string key) => _localizer[key];
            return
            [
                new(Name(nameof(Strings.Request_Step_Requested)), _localizer.Plural(nameof(Strings.Request_Step_Players_One), Math.Max(1, View.Votes)), true, false, "IconMessage", false),
                new(Name(nameof(Strings.Request_Step_Translator)), claim?.By ?? _localizer[nameof(Strings.Request_Step_Waiting)], reached >= 1, reached == 0, "IconGitHub", false),
                new(Name(nameof(Strings.Request_Step_Translating)), claim is { State: RequestClaimState.Working } c ? $"{c.Percent.ToString(_localizer.Culture)} %" : "—",
                    reached >= 2 && claim is not { State: RequestClaimState.Working }, claim is { State: RequestClaimState.Working }, "IconSparkles", false),
                new(Name(nameof(Strings.Request_Step_Published)), "—", false, reached == 3, "IconUpload", true),
            ];
        }
    }

    public bool HasClaim => View.Claim is not null;

    /// <summary>"alice is translating it: Translate · 45 %" / "alice translated it; publishing comes next" / "alice published it…".</summary>
    public string ClaimText => View.Claim switch
    {
        { State: RequestClaimState.Working } c => _localizer.Format(
            nameof(Strings.Request_Claim_Working), c.By, GenerationTexts.StepName(c.Stage ?? GenerationStage.Setup, _localizer), c.Percent.ToString(_localizer.Culture)),
        { State: RequestClaimState.Built } c => _localizer.Format(nameof(Strings.Request_Claim_Built), c.By),
        { } c => _localizer.Format(nameof(Strings.Request_Claim_Published), c.By),
        _ => _localizer[nameof(Strings.Request_Card_Waiting)],
    };

    /// <summary>"Last news 14.11.2026 18:00. Without news for 14 days the request opens again for others."</summary>
    public string ClaimDetail => View.Claim is { } claim
        ? _localizer.Format(nameof(Strings.Request_Claim_News), claim.Updated.ToLocalTime().ToString("g", _localizer.Culture))
        : string.Empty;

    public double ClaimPercent => View.Claim?.Percent ?? 0;

    public bool ShowClaimProgress => View.Claim is { State: RequestClaimState.Working };

    public IReadOnlyList<RequestNoteViewModel> Notes => [.. View.Notes.TakeLast(NotesShown).Select(n => new RequestNoteViewModel(n.Author ?? "?", n.Body.Trim()))];

    public bool HasNotes => View.Notes.Count > 0;

    // ---------- Done ----------

    /// <summary>The player's request has a pack now and they haven't closed the strip.</summary>
    public bool ShowDone => _hasPacks && _requests.Mine(_game.Id, _language) is { DoneAt: not null, DoneSeen: false };

    /// <summary>"Your request is done: the translation into Русский is here. After playing, tell others whether it works."</summary>
    public string DoneText => _localizer.Format(nameof(Strings.Request_Done_Text), LanguageName);

    /// <summary>Follows the page: the game, the tab's language, whether the language has packs, and the request's news.</summary>
    public void Refresh(Game game, string language, bool hasPacks)
    {
        var languageChanged = language != _language;
        _game = game;
        _language = language;
        _hasPacks = hasPacks;
        if (languageChanged || !hasPacks)
        {
            // Once per request while the app runs; its answer comes back through TranslationRequests.Changed.
            _ = _requests.SyncAsync(game.Id, language);
        }

        View = _requests.ViewOf(game.Id, language);
        OnPropertyChanged(string.Empty);
        AskCommand.NotifyCanExecuteChanged();
        WithdrawCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Ask() => _requests.ShowAskDialog(_game, _language);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Withdraw() => _requests.Withdraw(_game.Id, _language);

    [RelayCommand]
    private void Open()
    {
        if (View.Discussion is { } url)
        {
            _shell.OpenUrl(url);
        }
    }

    [RelayCommand]
    private void DismissDone() => _requests.DismissDone(_game.Id, _language);

    [RelayCommand]
    private void OpenGeneration() => _openGeneration();
}
