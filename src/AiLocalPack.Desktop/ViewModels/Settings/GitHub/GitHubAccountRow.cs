using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GitHub;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.GitHub;

/// <summary>
/// Settings → GitHub → Account (design §8; task 59). Signed in: avatar, login (name), "token kept in the system keychain",
/// <c>Profile ↗</c>, <c>Sign out</c>. Signed out: why an account is needed (publishing, rating; never installing) and
/// <c>Sign in with GitHub</c>, which opens the Device Flow panel (<see cref="SignIn"/>) inline. Shows the remembered
/// account at once (no network, no keychain) and asks GitHub on <see cref="CheckAsync"/>: GitHub out of reach keeps the
/// remembered account with a note, a revoked token signs out with an explanation, a keychain that can't be read says so
/// with <c>Check again</c>. After signing out here it points to GitHub's page where the app's access is revoked (the app
/// can't revoke it itself).
/// </summary>
public sealed class GitHubAccountRow : SettingRow
{
    private readonly IGitHubHost _host;
    private readonly ISystemShell _shell;
    private GitHubAccount? _account;
    private Bitmap? _avatar;
    private bool _unverified;
    private string? _keychainProblem;
    private string? _revokedLogin;
    private bool _signedOutHere;
    private bool _busy;
    private string? _error;
    private Task? _check;
    private int _generation;

    public GitHubAccountRow(SettingRowServices services, IGitHubHost host, ISystemShell shell, IUiDispatcher ui, GitHubSignInViewModel signIn)
        : base(services, nameof(Strings.Settings_GitHubAccount_Label), nameof(Strings.Settings_GitHubAccount_Keywords))
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(signIn);
        _host = host;
        _shell = shell;
        SignIn = signIn;
        SignInCommand = new AsyncRelayCommand(StartSignInAsync, () => CanSignIn);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => CanSignOut);
        CheckAgainCommand = new AsyncRelayCommand(CheckAsync, () => CanCheckAgain);
        OpenProfileCommand = new RelayCommand(() => _shell.OpenUrl(_account!.ProfileUrl), () => _account is not null);
        SignIn.StateChanged += (_, _) => Raise();
        SignIn.SignedIn += (_, account) =>
        {
            ++_generation;
            _signedOutHere = false;
            _revokedLogin = null;
            SetAccount(account, unverified: false, keychainProblem: null);
        };

        // A sign-in or sign-out elsewhere in the app (the publish dialog).
        host.Changed += (_, _) => ui.Post(() =>
        {
            // Our own sign-out reports through its result.
            var current = host.Account;
            if (!_busy && !SameLogin(current, _account))
            {
                ++_generation;
                SetAccount(current, unverified: false, keychainProblem: null);
            }
        });
        SetAccount(host.Account, unverified: false, keychainProblem: null);
    }

    /// <summary>Raised on the UI thread when the account or its problems change (the section updates its page).</summary>
    public event EventHandler? StateChanged;

    /// <summary>The Device Flow panel shown inline while signing in.</summary>
    public GitHubSignInViewModel SignIn { get; }

    public GitHubAccount? Account => _account;

    public bool IsSignedIn => _account is not null;

    public bool IsSignedOut => _account is null;

    /// <summary>The avatar once loaded; until then (or offline) the view draws <see cref="AvatarLetter"/>.</summary>
    public Bitmap? Avatar => _avatar;

    public bool HasAvatar => _avatar is not null;

    public string AvatarLetter => _account is { Login.Length: > 0 } a ? a.Login[..1].ToUpperInvariant() : "?";

    /// <summary>The login, or "Not signed in".</summary>
    public string Title => _account?.Login ?? Localizer[nameof(Strings.Settings_GitHubAccount_SignedOut)];

    /// <summary>The account's display name when it differs from the login.</summary>
    public string? FullName => _account?.Name is { Length: > 0 } name && name != _account.Login ? name : null;

    public bool HasFullName => FullName is not null;

    public override string? Help
    {
        get
        {
            if (_account is not null)
            {
                return Localizer.Format(nameof(Strings.Settings_GitHubAccount_SignedInHelp), GitHubTexts.KeychainName(Localizer, _host.KeychainKind));
            }

            if (_revokedLogin is { Length: > 0 } login)
            {
                return Localizer.Format(nameof(Strings.Settings_GitHubAccount_Revoked), login);
            }

            return Localizer[_signedOutHere ? nameof(Strings.Settings_GitHubAccount_SignedOutHere) : nameof(Strings.Settings_GitHubAccount_Why)];
        }
    }

    /// <summary>After signing out here: GitHub's page where the app's access is revoked.</summary>
    public override string? HelpLinkText => _signedOutHere && _account is null ? Localizer[nameof(Strings.Settings_GitHubAccount_Revoke)] : null;

    public override Uri? HelpLinkUri => _signedOutHere && _account is null ? _host.App.ConnectionsPage : null;

    /// <summary>A quiet note: GitHub couldn't be asked, or the keychain couldn't be read; <c>null</c> for none.</summary>
    public string? Warning => _keychainProblem is { } problem
        ? Localizer.Format(nameof(Strings.Settings_GitHubAccount_KeychainProblem), GitHubTexts.KeychainName(Localizer, _host.KeychainKind), problem)
        : _unverified && _account is not null ? Localizer[nameof(Strings.Settings_GitHubAccount_Unverified)] : null;

    public bool HasWarning => Warning is not null;

    /// <summary>Why the last action failed; <c>null</c> when it didn't.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public bool CanSignIn => _account is null && !_busy && !SignIn.IsActive;

    public bool CanSignOut => _account is not null && !_busy;

    public bool CanCheckAgain => _keychainProblem is not null && !_busy && !SignIn.IsActive;

    public IAsyncRelayCommand SignInCommand { get; }

    public IAsyncRelayCommand SignOutCommand { get; }

    public IAsyncRelayCommand CheckAgainCommand { get; }

    public IRelayCommand OpenProfileCommand { get; }

    public string SignInText => Localizer[nameof(Strings.Settings_GitHubAccount_SignIn)];

    public string SignOutText => Localizer[nameof(Strings.Settings_GitHubAccount_SignOut)];

    public string ProfileText => Localizer[nameof(Strings.Settings_GitHubAccount_Profile)];

    public string CheckAgainText => Localizer[nameof(Strings.Settings_Agent_CheckAgain)];

    public override IEnumerable<string> SearchTexts => _account is { } a ? [.. base.SearchTexts, a.Login] : base.SearchTexts;

    /// <summary>
    /// Asks GitHub whether the token still works (the section does it each time it opens). Quiet: the row keeps showing
    /// the remembered account until the answer comes. Skipped while signing in or out; a check already running is shared.
    /// </summary>
    public Task CheckAsync()
    {
        if (_busy || SignIn.IsBusy)
        {
            return Task.CompletedTask;
        }

        if (_check is { IsCompleted: false } running)
        {
            return running;
        }

        _check = RunCheckAsync(++_generation);
        return _check;
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Raise();
    }

    private async Task RunCheckAsync(int generation)
    {
        GitHubAccountCheck check;
        try
        {
            check = await _host.CheckAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogWarning(e, "Checking the GitHub sign-in failed");
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        switch (check)
        {
            case GitHubAccountCheck.SignedIn signedIn:
                SetAccount(signedIn.Account, unverified: false, keychainProblem: null);
                break;
            case GitHubAccountCheck.Unverified unverified:
                Services.Logger.LogInformation("GitHub sign-in not verified: {Detail}", unverified.Detail);
                SetAccount(unverified.Account, unverified: true, keychainProblem: null);
                break;
            case GitHubAccountCheck.SignedOut:
                SetAccount(null, unverified: false, keychainProblem: null);
                break;
            case GitHubAccountCheck.Revoked revoked:
                _revokedLogin = revoked.Login;
                _signedOutHere = false;
                SetAccount(null, unverified: false, keychainProblem: null);
                break;
            case GitHubAccountCheck.KeychainProblem problem:
                SetAccount(problem.Account, unverified: false, keychainProblem: Describe(problem.Keychain));
                break;
        }
    }

    private async Task StartSignInAsync()
    {
        Error = null;
        _signedOutHere = false;
        Raise();
        await SignIn.StartAsync();
    }

    /// <summary><c>Sign out</c>: deletes the token from the keychain and forgets the account; the keychain refusing keeps both.</summary>
    private async Task SignOutAsync()
    {
        var generation = ++_generation;
        _busy = true;
        Error = null;
        Raise();
        SecretResult deleted;
        try
        {
            deleted = await _host.SignOutAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogError(e, "Signing out of GitHub failed");
            deleted = SecretResult.Failed(e.Message);
        }
        finally
        {
            _busy = false;
        }

        if (generation != _generation)
        {
            Raise();
            return;
        }

        if (deleted.IsOk)
        {
            Services.Logger.LogInformation("Signed out of GitHub");
            _signedOutHere = true;
            _revokedLogin = null;
            SetAccount(null, unverified: false, keychainProblem: null);
        }
        else
        {
            Error = Localizer.Format(nameof(Strings.Settings_GitHubAccount_SignOutFailed), GitHubTexts.KeychainName(Localizer, _host.KeychainKind), Describe(deleted));
            Raise();
        }
    }

    private static string Describe(SecretResult result) => result.Problem ?? result.Status.ToString();

    private static bool SameLogin(GitHubAccount? a, GitHubAccount? b) => string.Equals(a?.Login, b?.Login, StringComparison.OrdinalIgnoreCase);

    private void SetAccount(GitHubAccount? account, bool unverified, string? keychainProblem)
    {
        var avatarChanged = account?.AvatarUrl != _account?.AvatarUrl;
        _account = account;
        _unverified = unverified;
        _keychainProblem = keychainProblem;
        if (avatarChanged || account is null)
        {
            _avatar = null;
            if (account?.AvatarUrl is { } url)
            {
                _ = LoadAvatarAsync(url);
            }
        }

        Raise();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadAvatarAsync(Uri url)
    {
        var avatar = await _host.LoadAvatarAsync(url);
        if (avatar is null || _account?.AvatarUrl != url)
        {
            return;
        }

        _avatar = avatar;
        OnPropertyChanged(nameof(Avatar));
        OnPropertyChanged(nameof(HasAvatar));
    }

    private void Raise()
    {
        foreach (var name in (string[])[nameof(Account), nameof(IsSignedIn), nameof(IsSignedOut), nameof(Avatar), nameof(HasAvatar), nameof(AvatarLetter),
                     nameof(Title), nameof(FullName), nameof(HasFullName), nameof(Label), nameof(Help), nameof(HelpLinkText), nameof(HelpLinkUri), nameof(HasHelpLink),
                     nameof(Warning), nameof(HasWarning), nameof(CanSignIn), nameof(CanSignOut), nameof(CanCheckAgain),
                     nameof(SignInText), nameof(SignOutText), nameof(ProfileText), nameof(CheckAgainText)])
        {
            OnPropertyChanged(name);
        }

        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();
        OpenProfileCommand.NotifyCanExecuteChanged();
    }
}
