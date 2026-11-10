using System.Globalization;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GitHub;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Publishing;

/// <summary>What the first-time dialog still waits for (its hint on the left of <c>Continue</c>).</summary>
public enum PublishSignInMissing
{
    /// <summary>Nothing: <c>Continue</c> is on.</summary>
    None,

    /// <summary>GitHub hasn't confirmed the sign-in yet.</summary>
    SignIn,

    /// <summary>The license box isn't ticked.</summary>
    License,
}

/// <summary>The publish dialogs' rules (pure; design §7).</summary>
public static class PublishRules
{
    /// <summary>The first-time dialog is needed: no GitHub account here, or the license was never accepted.</summary>
    public static bool NeedsSignInDialog(bool signedIn, bool licenseAccepted) => !signedIn || !licenseAccepted;

    /// <summary>What <c>Continue</c> still waits for: GitHub first, then the license box.</summary>
    public static PublishSignInMissing Missing(bool signedIn, bool licenseAccepted) =>
        !signedIn ? PublishSignInMissing.SignIn : !licenseAccepted ? PublishSignInMissing.License : PublishSignInMissing.None;

    /// <summary><c>Continue</c> is on once GitHub confirmed the sign-in and the box is ticked.</summary>
    public static bool CanContinue(bool signedIn, bool licenseAccepted) => Missing(signedIn, licenseAccepted) == PublishSignInMissing.None;

    /// <summary>The similar-pack dialog's preselected choice: the new revision when it is recommended, else a separate pack.</summary>
    public static PublishTarget DefaultTarget(SimilarPack similar)
    {
        ArgumentNullException.ThrowIfNull(similar);
        return similar.RevisionRecommended ? new PublishTarget.RevisionOf(similar.Pack.Id) : new PublishTarget.NewPack();
    }
}

/// <summary>The first-time dialog's continuation: runs after the dialog closed with <c>Continue</c>.</summary>
public sealed record PublishSignInRequest(Action OnContinue);

/// <summary>
/// "Publish to the ailocalpack registry" (design §7, first time): GitHub sign-in (the reusable Device Flow panel, started
/// at once when signed out) and the CC BY-SA 4.0 consent in one dialog. <c>Continue</c> is on once GitHub confirmed and
/// the box is ticked; the hint says what is still missing. <c>Continue</c> remembers the consent
/// (<see cref="AppSettings.PublishLicenseAccepted"/>). Cancel, ✕ and Esc stop the sign-in.
/// </summary>
public sealed partial class PublishSignInDialogViewModel : DialogViewModel
{
    private readonly PublishSignInRequest _request;
    private readonly IDialogService _dialogs;
    private readonly IGitHubHost _github;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private bool _continued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue), nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _isLicenseAccepted;

    public PublishSignInDialogViewModel(
        PublishSignInRequest request,
        IDialogService dialogs,
        IGitHubHost github,
        GitHubSignInViewModel signIn,
        SettingsStore settings,
        Localizer localizer,
        IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _dialogs = dialogs;
        _github = github;
        _settings = settings;
        _localizer = localizer;
        _ui = ui;
        SignIn = signIn;
        _isLicenseAccepted = settings.Current.PublishLicenseAccepted;
        SignIn.StateChanged += (_, _) => RaiseAccount();
        SignIn.SignedIn += (_, _) => RaiseAccount();
        _github.Changed += OnGitHubChanged;
        if (_github.Account is null)
        {
            _ = SignIn.StartAsync();
        }
    }

    /// <summary>The Device Flow panel (code, Copy, Open browser, waiting, Try again).</summary>
    public GitHubSignInViewModel SignIn { get; }

    public bool IsSignedIn => _github.Account is not null;

    /// <summary>"Signed in as octocat".</summary>
    public string SignedInText => _github.Account is { } account ? _localizer.Format(nameof(Strings.Publish_SignIn_SignedInAs), account.Login) : string.Empty;

    /// <summary><c>Sign in with GitHub</c> while signed out and the panel isn't shown (the player cancelled it).</summary>
    public bool ShowSignInButton => !IsSignedIn && SignIn.State is GitHubSignInState.Idle or GitHubSignInState.SignedIn;

    public bool CanContinue => PublishRules.CanContinue(IsSignedIn, IsLicenseAccepted);

    /// <summary>"Waiting for GitHub…" / "Sign in with GitHub to continue" / "Accept the license to continue", or empty.</summary>
    public string Hint => PublishRules.Missing(IsSignedIn, IsLicenseAccepted) switch
    {
        PublishSignInMissing.SignIn => _localizer[SignIn.IsBusy ? nameof(Strings.Publish_SignIn_HintWaiting) : nameof(Strings.Publish_SignIn_HintSignIn)],
        PublishSignInMissing.License => _localizer[nameof(Strings.Publish_SignIn_HintLicense)],
        _ => string.Empty,
    };

    public override void OnClosed()
    {
        _github.Changed -= OnGitHubChanged;
        if (SignIn.IsBusy)
        {
            SignIn.Cancel();
        }
    }

    [RelayCommand]
    private Task StartSignIn() => SignIn.StartAsync();

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        if (_continued)
        {
            return;
        }

        _continued = true;
        if (!_settings.Current.PublishLicenseAccepted)
        {
            _settings.Update(s => s with { PublishLicenseAccepted = true });
        }

        _dialogs.Close(this);
        _request.OnContinue();
    }

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);

    private void OnGitHubChanged(object? sender, EventArgs e) => _ui.Post(RaiseAccount);

    private void RaiseAccount()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignedInText));
        OnPropertyChanged(nameof(ShowSignInButton));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(Hint));
        ContinueCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>The similar-pack dialog's question: the local pack, the registry pack like it, and what to do with the choice.</summary>
/// <param name="OnPublish">Runs after the dialog closed with <c>Publish</c>.</param>
public sealed record SimilarPackRequest(LocalPack Pack, SimilarPack Similar, Action<PublishTarget> OnPublish);

/// <summary>
/// "A pack for Русский already exists" (design §7): the registry pack (author, revision, status, 👍, coverage, the build
/// it was made for) and two radio cards: <i>New revision of «…» (rev N+1)</i>, marked <i>Recommended</i> by
/// <see cref="SimilarPacks.Find"/>'s rule, and <i>A separate pack</i>. The recommended one is preselected
/// (<see cref="PublishRules.DefaultTarget"/>). <c>Cancel</c> / <c>Publish</c>.
/// </summary>
public sealed partial class SimilarPackDialogViewModel : DialogViewModel
{
    private readonly SimilarPackRequest _request;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private bool _published;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeparateChosen))]
    private bool _isRevisionChosen;

    public SimilarPackDialogViewModel(SimilarPackRequest request, IDialogService dialogs, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _dialogs = dialogs;
        _localizer = localizer;
        _isRevisionChosen = PublishRules.DefaultTarget(request.Similar) is PublishTarget.RevisionOf;
    }

    private Core.Registry.RegistryPack Existing => _request.Similar.Pack;

    private int NextRevision => Existing.Revision + 1;

    public bool IsSeparateChosen
    {
        get => !IsRevisionChosen;
        set => IsRevisionChosen = !value;
    }

    /// <summary>"A pack for Русский already exists".</summary>
    public string Title => _localizer.Format(nameof(Strings.Publish_Similar_Title), Core.Languages.LanguageNames.Native(Existing.Language));

    /// <summary>The existing pack's author's initial (the avatar letter).</summary>
    public string Initial => Existing.Author.GitHub.Length > 0 ? Existing.Author.GitHub[..1].ToUpperInvariant() : "?";

    /// <summary>"«Неон» by alice · rev 3".</summary>
    public string ExistingTitle => _localizer.Format(nameof(Strings.Publish_Similar_Existing), Existing.Title, Existing.Author.GitHub, Existing.Revision);

    /// <summary>"verified", "alpha".</summary>
    public string StatusText => _localizer[Existing.Status == PackStatus.Verified ? nameof(Strings.Library_Card_Verified) : nameof(Strings.Library_Card_Alpha)];

    public bool IsVerified => Existing.Status == PackStatus.Verified;

    public string LikesText => Existing.Rating.Likes.ToString("N0", _localizer.Culture);

    /// <summary>"96% coverage".</summary>
    public string CoverageText => _localizer.Format(
        nameof(Strings.Publish_Similar_Coverage),
        Math.Round(Existing.Coverage.Ratio * 100, MidpointRounding.AwayFromZero).ToString("0", _localizer.Culture));

    /// <summary>"built for 9087113", "built for 3 builds", or empty when the pack lists none.</summary>
    public string BuildText => Existing.BuildIds.Count switch
    {
        0 when Existing.BuildCount == 0 => string.Empty,
        1 => _localizer.Format(nameof(Strings.Publish_Similar_Build), Existing.BuildIds[0]),
        _ => _localizer.Plural(nameof(Strings.Publish_Similar_Builds_One), Math.Max(Existing.BuildCount, Existing.BuildIds.Count)),
    };

    public bool HasBuild => BuildText.Length > 0;

    /// <summary>The existing pack is the one the local pack updates (task 56).</summary>
    public bool IsBase => _request.Similar.IsBase;

    public bool IsRecommended => _request.Similar.RevisionRecommended;

    /// <summary>"New revision of «Неон» (rev 4)".</summary>
    public string RevisionTitle => _localizer.Format(nameof(Strings.Publish_Similar_RevisionTitle), Existing.Title, NextRevision.ToString(CultureInfo.InvariantCulture));

    /// <summary>"Its players get your version as an update. alice stays the author, you're credited for rev 4. Status starts over at alpha."</summary>
    public string RevisionText => _localizer.Format(nameof(Strings.Publish_Similar_RevisionText), Existing.Author.GitHub, NextRevision.ToString(CultureInfo.InvariantCulture));

    public string SeparateTitle => _localizer[nameof(Strings.Publish_Similar_SeparateTitle)];

    /// <summary>"«Курьер 2087» is listed next to «Неон»; players choose. …".</summary>
    public string SeparateText => _localizer.Format(nameof(Strings.Publish_Similar_SeparateText), _request.Pack.Manifest.Title, Existing.Title);

    /// <summary>What <c>Publish</c> sends now.</summary>
    public PublishTarget Target => IsRevisionChosen ? new PublishTarget.RevisionOf(Existing.Id) : new PublishTarget.NewPack();

    [RelayCommand]
    private void Publish()
    {
        if (_published)
        {
            return;
        }

        _published = true;
        var target = Target;
        _dialogs.Close(this);
        _request.OnPublish(target);
    }

    [RelayCommand]
    private void ChooseRevision() => IsRevisionChosen = true;

    [RelayCommand]
    private void ChooseSeparate() => IsRevisionChosen = false;

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);
}
