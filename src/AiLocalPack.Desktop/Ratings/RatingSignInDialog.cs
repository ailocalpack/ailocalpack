using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GitHub;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Ratings;

/// <summary>What the sign-in is for: the dialog's title and intro say so.</summary>
public enum RatingSignInPurpose
{
    /// <summary>A like or a works / doesn't-work report (task 63).</summary>
    Rating,

    /// <summary>A translation request, a vote or a claim (task 74).</summary>
    Request,
}

/// <summary>The rating sign-in dialog's continuation: runs once GitHub confirmed the sign-in.</summary>
public sealed record RatingSignInRequest(Action OnSignedIn, RatingSignInPurpose Purpose = RatingSignInPurpose.Rating);

/// <summary>
/// "Sign in to rate packs" (task 63; design §4: GitHub sign-in on first use): the reusable Device Flow panel, started at
/// once. When GitHub confirms, the dialog closes by itself and the like or report the player pressed is sent. Cancel,
/// ✕ and Esc stop the sign-in and send nothing.
/// </summary>
public sealed partial class RatingSignInDialogViewModel : DialogViewModel
{
    private readonly RatingSignInRequest _request;
    private readonly IDialogService _dialogs;
    private readonly IGitHubHost _github;
    private readonly IUiDispatcher _ui;
    private readonly Localizer _localizer;
    private bool _done;

    public RatingSignInDialogViewModel(RatingSignInRequest request, IDialogService dialogs, IGitHubHost github, GitHubSignInViewModel signIn, IUiDispatcher ui, Localizer localizer)
    {
        _localizer = localizer;
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _dialogs = dialogs;
        _github = github;
        _ui = ui;
        SignIn = signIn;
        SignIn.SignedIn += (_, _) => _ui.Post(Continue);
        SignIn.StateChanged += (_, _) => OnPropertyChanged(nameof(ShowSignInButton));
        if (_github.Account is null)
        {
            _ = SignIn.StartAsync();
        }
    }

    /// <summary>"Sign in to rate packs" / "Sign in to ask for translations".</summary>
    public string TitleText => _localizer[_request.Purpose == RatingSignInPurpose.Request ? nameof(Strings.Request_SignIn_Title) : nameof(Strings.Rating_SignIn_Title)];

    public string IntroText => _localizer[_request.Purpose == RatingSignInPurpose.Request ? nameof(Strings.Request_SignIn_Intro) : nameof(Strings.Rating_SignIn_Intro)];

    /// <summary>The Device Flow panel (code, Copy, Open browser, waiting, Try again).</summary>
    public GitHubSignInViewModel SignIn { get; }

    /// <summary><c>Sign in with GitHub</c> again after the player cancelled the panel.</summary>
    public bool ShowSignInButton => _github.Account is null && SignIn.State is GitHubSignInState.Idle;

    public override void OnClosed()
    {
        if (SignIn.IsBusy)
        {
            SignIn.Cancel();
        }
    }

    [RelayCommand]
    private Task StartSignIn() => SignIn.StartAsync();

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);

    private void Continue()
    {
        if (_done || _github.Account is null)
        {
            return;
        }

        _done = true;
        _dialogs.Close(this);
        _request.OnSignedIn();
    }
}

/// <summary>The texts of ratings in the UI language (pure, so tests check them).</summary>
public static class RatingTexts
{
    /// <summary>Why a like or report didn't go through, in plain words.</summary>
    public static string Error(Core.Ratings.RatingError error, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(localizer);
        return error.Problem switch
        {
            Core.Ratings.RatingProblem.NotSignedIn => localizer[nameof(Strings.Rating_Error_NotSignedIn)],
            Core.Ratings.RatingProblem.Keychain => localizer[nameof(Strings.Rating_Error_Keychain)],
            Core.Ratings.RatingProblem.Unauthorized => localizer[nameof(Strings.Rating_Error_Unauthorized)],
            Core.Ratings.RatingProblem.NotFound => localizer[nameof(Strings.Rating_Error_NotFound)],
            Core.Ratings.RatingProblem.Locked => localizer[nameof(Strings.Rating_Error_Locked)],
            Core.Ratings.RatingProblem.Forbidden => localizer.Format(nameof(Strings.Rating_Error_Forbidden), error.Message),
            Core.Ratings.RatingProblem.RateLimited when error.RetryAt is { } at =>
                localizer.Format(nameof(Strings.Rating_Error_RateLimitedUntil), at.ToLocalTime().ToString("t", localizer.Culture)),
            Core.Ratings.RatingProblem.RateLimited => localizer[nameof(Strings.Rating_Error_RateLimited)],
            Core.Ratings.RatingProblem.Network => localizer[nameof(Strings.Rating_Error_Network)],
            _ => localizer.Format(nameof(Strings.Rating_Error_Unexpected), error.Message),
        };
    }
}
