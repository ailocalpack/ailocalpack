using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Requests;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Requests;

/// <summary>What the request dialog asks for: the game and the language.</summary>
public sealed record RequestDialogRequest(Game Game, string Language);

/// <summary>
/// "Request a translation" (task 74; design: requests, variant 1): the game, the language, how many already asked, an
/// optional public note for the translator and where it goes (the registry's Requests on GitHub, under the player's
/// name). <c>Request translation</c> closes the dialog and sends (<see cref="TranslationRequests.Ask"/>; the sign-in comes
/// first when needed). Cancel, ✕ and Esc send nothing.
/// </summary>
public sealed partial class RequestDialogViewModel : DialogViewModel
{
    private readonly RequestDialogRequest _request;
    private readonly TranslationRequests _requests;
    private readonly IDialogService _dialogs;
    private readonly IGitHubHost _github;
    private readonly Localizer _localizer;

    [ObservableProperty]
    private string _note = string.Empty;

    public RequestDialogViewModel(RequestDialogRequest request, TranslationRequests requests, IDialogService dialogs, IGitHubHost github, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _requests = requests;
        _dialogs = dialogs;
        _github = github;
        _localizer = localizer;
        View = requests.ViewOf(request.Game.Id, request.Language);
    }

    /// <summary>The request as it is now (how many asked).</summary>
    public RequestView View { get; }

    public string GameName => _request.Game.Name;

    /// <summary>"Unity · Steam · build 14882031".</summary>
    public string GameFacts => string.Join(" · ", new[]
    {
        Toolkit.Engines.EngineKind.TryParse(_request.Game.Engine, out var engine) && engine.Engine != Toolkit.Engines.GameEngine.Unknown ? engine.DisplayName : null,
        _request.Game.Source == GameSource.Steam ? "Steam" : _localizer[nameof(Strings.Library_Card_Manual)],
        _request.Game.Version?.BuildId is { } build ? _localizer.Format(nameof(Strings.Request_Dialog_Build), build) : null,
    }.OfType<string>());

    /// <summary>"Русский".</summary>
    public string LanguageName => LanguageNames.Native(_request.Language);

    /// <summary>"12 players already asked for this. Your request adds your vote and your note."</summary>
    public string OthersText => _localizer.Plural(nameof(Strings.Request_Dialog_Others_One), View.Others);

    public bool HasOthers => View.Others > 0;

    /// <summary>"Posted as @mkzm to the registry's Requests on GitHub." / "Sign in to GitHub comes next…".</summary>
    public string GitHubLine => _github.Account is { } account
        ? _localizer.Format(nameof(Strings.Request_Dialog_PostedAs), account.Login)
        : _localizer[nameof(Strings.Request_Dialog_SignInNext)];

    public int MaxNoteLength => RequestRules.MaxNoteLength;

    [RelayCommand]
    private void Send()
    {
        _dialogs.Close(this);
        _requests.Ask(_request.Game, _request.Language, Note);
    }

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);
}
