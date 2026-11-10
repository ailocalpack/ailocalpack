using AiLocalPack.Core.GitHub;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.GitHub;

/// <summary>Where a GitHub Device Flow sign-in stands.</summary>
public enum GitHubSignInState
{
    /// <summary>Not started, or cancelled: the panel is hidden.</summary>
    Idle,

    /// <summary>Asking GitHub for a code.</summary>
    Requesting,

    /// <summary>The code is shown; waiting for the player to approve it on GitHub.</summary>
    Waiting,

    /// <summary>Approved, the token is in the keychain: the panel is hidden.</summary>
    SignedIn,

    /// <summary>It didn't work (offline, expired, denied, the keychain refused): the reason and <c>Try again</c>.</summary>
    Failed,
}

/// <summary>
/// The reusable GitHub Device Flow panel (design §7–§8; task 59), used inline in Settings → GitHub, by the publish
/// dialog (task 61) and by rating (task 63). The host calls <see cref="StartAsync"/>; the panel shows the two steps
/// (open the address, enter the code) with <c>Copy</c> and <c>Open browser</c>, waits with a spinner, and ends in
/// <see cref="SignedIn"/> or in a reason with <c>Try again</c>. <c>Cancel</c> stops the wait and hides it. Results that
/// arrive after a cancel or a new start are dropped.
/// </summary>
public sealed class GitHubSignInViewModel : ObservableObject
{
    private readonly IGitHubHost _host;
    private readonly ISystemShell _shell;
    private readonly Localizer _localizer;
    private readonly ILogger<GitHubSignInViewModel> _logger;
    private GitHubSignInState _state;
    private DeviceAuthorization? _authorization;
    private string? _error;
    private bool _copied;
    private CancellationTokenSource? _cancel;
    private int _generation;

    public GitHubSignInViewModel(IGitHubHost host, ISystemShell shell, Localizer localizer, ILogger<GitHubSignInViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(localizer);
        _host = host;
        _shell = shell;
        _localizer = localizer;
        _logger = logger;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => IsWaiting);
        OpenBrowserCommand = new RelayCommand(OpenBrowser, () => IsWaiting);
        CancelCommand = new RelayCommand(Cancel, () => IsActive);
        _localizer.CultureChanged += (_, _) => RaiseTexts();
    }

    /// <summary>Raised on the UI thread once GitHub approved and the token is kept.</summary>
    public event EventHandler<GitHubAccount>? SignedIn;

    /// <summary>Raised when the state changes (hosts follow <see cref="IsActive"/>).</summary>
    public event EventHandler? StateChanged;

    public GitHubSignInState State => _state;

    /// <summary>The panel is shown: asking, waiting or failed.</summary>
    public bool IsActive => State is GitHubSignInState.Requesting or GitHubSignInState.Waiting or GitHubSignInState.Failed;

    /// <summary>Asking for a code or waiting for the approval.</summary>
    public bool IsBusy => State is GitHubSignInState.Requesting or GitHubSignInState.Waiting;

    public bool IsRequesting => State == GitHubSignInState.Requesting;

    public bool IsWaiting => State == GitHubSignInState.Waiting;

    public bool IsFailed => State == GitHubSignInState.Failed;

    /// <summary>The code to enter on GitHub ("WDJB-MJHT"); <c>null</c> before GitHub gave one.</summary>
    public string? UserCode => _authorization?.UserCode;

    public Uri? VerificationUri => _authorization?.VerificationUri;

    /// <summary>"① Open github.com/login/device in your browser".</summary>
    public string StepOpenText => _localizer.Format(
        nameof(Strings.GitHubSignIn_StepOpen),
        _authorization is { } a ? GitHubTexts.ShortAddress(a.VerificationUri) : GitHubTexts.ShortAddress(new Uri(_host.App.Web, "login/device")));

    public string StepEnterText => _localizer[nameof(Strings.GitHubSignIn_StepEnter)];

    public string StatusText => State switch
    {
        GitHubSignInState.Requesting => _localizer[nameof(Strings.GitHubSignIn_Requesting)],
        _ => _localizer[nameof(Strings.GitHubSignIn_Waiting)],
    };

    /// <summary>Why the sign-in didn't work, in the UI language.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public string CopyText => _localizer[_copied ? nameof(Strings.GitHubSignIn_Copied) : nameof(Strings.GitHubSignIn_Copy)];

    public string OpenBrowserText => _localizer[nameof(Strings.GitHubSignIn_OpenBrowser)];

    public string CancelText => _localizer[nameof(Strings.GitHubSignIn_Cancel)];

    public string TryAgainText => _localizer[nameof(Strings.GitHubSignIn_TryAgain)];

    public IAsyncRelayCommand StartCommand { get; }

    public IAsyncRelayCommand CopyCommand { get; }

    public IRelayCommand OpenBrowserCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>Asks GitHub for a code, shows it and waits for the approval (or a cancel). Starting again drops the previous attempt.</summary>
    public async Task StartAsync()
    {
        _cancel?.Cancel();
        var generation = ++_generation;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _authorization = null;
        _copied = false;
        Error = null;
        SetState(GitHubSignInState.Requesting);
        try
        {
            var started = await _host.StartSignInAsync(cancel.Token);
            if (generation != _generation)
            {
                return;
            }

            if (started is DeviceCodeResult.Failed failed)
            {
                _logger.LogWarning("GitHub sign-in: no code ({Problem}: {Detail})", failed.Problem, failed.Detail);
                Fail(GitHubTexts.Problem(_localizer, failed.Problem, failed.Detail));
                return;
            }

            _authorization = ((DeviceCodeResult.Started)started).Authorization;
            SetState(GitHubSignInState.Waiting);
            var result = await _host.FinishSignInAsync(_authorization, cancel.Token);
            if (generation != _generation)
            {
                return;
            }

            Finish(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("GitHub sign-in cancelled");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GitHub sign-in failed");
            if (generation == _generation)
            {
                Fail(_localizer.Format(nameof(Strings.GitHubSignIn_Error_Refused), e.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_cancel, cancel))
            {
                _cancel = null;
            }
        }
    }

    /// <summary>Stops waiting and hides the panel; GitHub's code simply expires.</summary>
    public void Cancel()
    {
        ++_generation;
        _cancel?.Cancel();
        _authorization = null;
        Error = null;
        SetState(GitHubSignInState.Idle);
    }

    private void Finish(GitHubSignInResult result)
    {
        switch (result)
        {
            case GitHubSignInResult.SignedIn signedIn:
                _authorization = null;
                SetState(GitHubSignInState.SignedIn);
                SignedIn?.Invoke(this, signedIn.Account);
                break;
            case GitHubSignInResult.Expired:
                Fail(_localizer[nameof(Strings.GitHubSignIn_Error_Expired)]);
                break;
            case GitHubSignInResult.Denied:
                Fail(_localizer[nameof(Strings.GitHubSignIn_Error_Denied)]);
                break;
            case GitHubSignInResult.NotSaved notSaved:
                Fail(_localizer.Format(
                    nameof(Strings.GitHubSignIn_Error_NotSaved),
                    notSaved.User.Login,
                    GitHubTexts.KeychainName(_localizer, _host.KeychainKind),
                    notSaved.Keychain.Problem ?? notSaved.Keychain.Status.ToString()));
                break;
            case GitHubSignInResult.Failed failed:
                Fail(GitHubTexts.Problem(_localizer, failed.Problem, failed.Detail));
                break;
        }
    }

    private void Fail(string error)
    {
        _authorization = null;
        Error = error;
        SetState(GitHubSignInState.Failed);
    }

    private async Task CopyAsync()
    {
        if (UserCode is not { } code)
        {
            return;
        }

        if (await _shell.CopyTextAsync(code))
        {
            _copied = true;
            OnPropertyChanged(nameof(CopyText));
        }
    }

    private void OpenBrowser()
    {
        if (VerificationUri is { } uri)
        {
            _shell.OpenUrl(uri);
        }
    }

    private void SetState(GitHubSignInState state)
    {
        var changed = _state != state;
        _state = state;
        foreach (var name in (string[])[nameof(State), nameof(IsActive), nameof(IsBusy), nameof(IsRequesting), nameof(IsWaiting), nameof(IsFailed),
                     nameof(UserCode), nameof(VerificationUri)])
        {
            OnPropertyChanged(name);
        }

        RaiseTexts();
        StartCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        OpenBrowserCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(StepOpenText));
        OnPropertyChanged(nameof(StepEnterText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(OpenBrowserText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(TryAgainText));
    }
}
