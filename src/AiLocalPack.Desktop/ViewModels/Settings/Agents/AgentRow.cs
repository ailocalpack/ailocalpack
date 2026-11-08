using AiLocalPack.Agents;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>
/// One agent of Settings → AI agents (design §8): logo, status badge, source, version and account, and the actions of
/// its state: <c>Install</c> → <em>Installing N%</em> (<c>Cancel</c>) → <em>Installed · not signed in</em> →
/// <c>Sign in</c> → <em>Waiting for sign-in</em> (<c>Cancel</c>) → <em>Ready</em> (<c>Update to X</c> for the app's copy
/// when there is one, <c>Sign out</c>). An agent seen signed in before and signed out now (not by the player here) is
/// <em>Session expired</em>, the section's warning. The app's operations go through <see cref="IAgentHost"/>; results
/// come back on the UI thread (<see cref="IUiDispatcher"/>), and a check that finishes after an action started is dropped.
/// </summary>
public sealed class AgentRow : SettingRow
{
    private readonly IAgentHost _host;
    private readonly IUiDispatcher _ui;
    private AgentRowState _state = AgentRowState.Checking;
    private AgentInstallation? _installation;
    private AgentAuthStatus? _auth;
    private string? _updateVersion;
    private long? _releaseSize;
    private AgentInstallStage _stage;
    private int _percent;
    private string? _error;
    private CancellationTokenSource? _cancel;
    private Task? _check;
    private int _generation;

    public AgentRow(SettingRowServices services, IAgentHost host, IUiDispatcher ui, AgentInfo info)
        : base(services, nameof(Strings.Settings_Agent_Label), nameof(Strings.Settings_Agent_Keywords))
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(info);
        _host = host;
        _ui = ui;
        Info = info;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => State == AgentRowState.NotInstalled);
        UpdateCommand = new AsyncRelayCommand(InstallAsync, () => CanUpdate);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => CanSignIn);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => CanSignOut);
        CheckAgainCommand = new AsyncRelayCommand(CheckAsync, () => CanCheckAgain);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => CanCancel);
    }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes (the section recounts alerts and the default agent).</summary>
    public event EventHandler? StateChanged;

    public AgentInfo Info { get; }

    public string AgentId => Info.Id;

    public AgentRowState State => _state;

    /// <summary>The program in use, once found.</summary>
    public AgentInstallation? Installation => _installation;

    public bool IsReady => State == AgentRowState.Ready;

    /// <summary>A newer build of the app's own copy, when there is one.</summary>
    public string? UpdateVersion => _updateVersion;

    /// <summary>Percent of the download while installing or updating.</summary>
    public int Percent => _percent;

    /// <summary>Why the last action failed, in the UI language; <c>null</c> when it didn't.</summary>
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

    public IAsyncRelayCommand InstallCommand { get; }

    public IAsyncRelayCommand UpdateCommand { get; }

    public IAsyncRelayCommand SignInCommand { get; }

    public IAsyncRelayCommand SignOutCommand { get; }

    public IAsyncRelayCommand CheckAgainCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public bool CanInstall => State == AgentRowState.NotInstalled;

    public bool CanUpdate => _updateVersion is not null && _installation?.Source == AgentSource.Managed && !AgentRowStates.IsBusy(State);

    public bool CanSignIn => State is AgentRowState.NotSignedIn or AgentRowState.SessionExpired or AgentRowState.SignInUnknown;

    public bool CanSignOut => State == AgentRowState.Ready;

    public bool CanCheckAgain => State is AgentRowState.SignInUnknown or AgentRowState.TooOld;

    public bool CanCancel => State is AgentRowState.Installing or AgentRowState.Updating or AgentRowState.SigningIn;

    public bool HasProgress => State is AgentRowState.Installing or AgentRowState.Updating;

    public override string Label => Info.Name;

    /// <summary>Status badge text ("Ready · signed in", "Installing · 40%").</summary>
    public string StatusText => State switch
    {
        AgentRowState.Checking => Localizer[nameof(Strings.Settings_Agent_Status_Checking)],
        AgentRowState.NotInstalled => Localizer[nameof(Strings.Settings_Agent_Status_NotInstalled)],
        AgentRowState.Installing => Localizer.Format(nameof(Strings.Settings_Agent_Status_Installing), _percent),
        AgentRowState.Updating => Localizer.Format(nameof(Strings.Settings_Agent_Status_Updating), _updateVersion, _percent),
        AgentRowState.TooOld => Localizer.Format(nameof(Strings.Settings_Agent_Status_TooOld), _installation?.Version),
        AgentRowState.NotSignedIn => Localizer[nameof(Strings.Settings_Agent_Status_NotSignedIn)],
        AgentRowState.SessionExpired => Localizer[nameof(Strings.Settings_Agent_Status_Expired)],
        AgentRowState.SignInUnknown => Localizer[nameof(Strings.Settings_Agent_Status_Unknown)],
        AgentRowState.SigningIn => Localizer[nameof(Strings.Settings_Agent_Status_SigningIn)],
        _ => Localizer[nameof(Strings.Settings_Agent_Status_Ready)],
    };

    public bool IsStatusOk => State == AgentRowState.Ready;

    public bool IsStatusWarn => State is AgentRowState.SessionExpired or AgentRowState.TooOld;

    public bool IsStatusBusy => AgentRowStates.IsBusy(State);

    public bool IsStatusAccent => State is AgentRowState.NotSignedIn or AgentRowState.SignInUnknown;

    /// <summary>The badge is tinted with the accent colour: something runs or the player has a step to take.</summary>
    public bool IsStatusTinted => IsStatusBusy || IsStatusAccent;

    /// <summary>"Found on PATH · /usr/local/bin/claude · v2.1.319 · you@example.com"; empty without a program.</summary>
    public string SourceText
    {
        get
        {
            if (_installation is not { } installation)
            {
                return string.Empty;
            }

            var parts = new List<string>
            {
                installation.Source == AgentSource.Managed
                    ? Localizer[nameof(Strings.Settings_Agent_Source_Managed)]
                    : Localizer.Format(nameof(Strings.Settings_Agent_Source_Path), installation.Executable),
            };
            if (installation.Version is { } version)
            {
                parts.Add("v" + version);
            }

            if (State == AgentRowState.Ready && _auth?.Account is { Length: > 0 } account)
            {
                parts.Add(Localizer.Format(nameof(Strings.Settings_Agent_Account), account));
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasSource => SourceText.Length > 0;

    public override string? Help => State switch
    {
        AgentRowState.NotInstalled => Localizer.Format(
            Info.InstallHelpKey,
            _releaseSize is { } size ? " (" + AddGameText.FormatSize(Localizer, size) + ")" : string.Empty),
        AgentRowState.Installing or AgentRowState.Updating => _stage switch
        {
            AgentInstallStage.Resolving => Localizer[nameof(Strings.Settings_Agent_Stage_Resolving)],
            AgentInstallStage.Downloading => Localizer.Format(nameof(Strings.Settings_Agent_Stage_Downloading), Info.Vendor),
            _ => Localizer[nameof(Strings.Settings_Agent_Stage_Unpacking)],
        },
        AgentRowState.TooOld => Localizer.Format(
            _installation?.Source == AgentSource.Managed ? nameof(Strings.Settings_Agent_TooOld_Managed) : nameof(Strings.Settings_Agent_TooOld_Own),
            ClaudeCodeCommandLine.MinimumVersion.ToString(3)),
        AgentRowState.NotSignedIn or AgentRowState.SessionExpired => Localizer[Info.SignInHelpKey],
        AgentRowState.SignInUnknown => Localizer.Format(nameof(Strings.Settings_Agent_UnknownHelp), _auth?.Detail ?? "?"),
        AgentRowState.SigningIn => Localizer.Format(nameof(Strings.Settings_Agent_SigningInHelp), Info.Name),
        _ => null,
    };

    public string InstallText => Localizer[nameof(Strings.Settings_Agent_Install)];

    public string UpdateText => Localizer.Format(nameof(Strings.Settings_Agent_Update), _updateVersion);

    public string SignInText => Localizer[nameof(Strings.Settings_Agent_SignIn)];

    public string SignOutText => Localizer[nameof(Strings.Settings_Agent_SignOut)];

    public string CheckAgainText => Localizer[nameof(Strings.Settings_Agent_CheckAgain)];

    public string CancelText => Localizer[nameof(Strings.Settings_Agent_Cancel)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Info.Name, Info.Vendor];

    /// <summary>
    /// Finds the program, its sign-in and (for the app's copy, or when nothing is installed) the current release. Quiet:
    /// the row keeps what it shows until the answer comes, except on the first check. Skipped while an action runs; a
    /// check already running is shared.
    /// </summary>
    public Task CheckAsync()
    {
        if (State != AgentRowState.Checking && AgentRowStates.IsBusy(State))
        {
            return Task.CompletedTask;
        }

        if (_check is { IsCompleted: false } running)
        {
            return running;
        }

        _check = RunCheckAsync(_generation);
        return _check;
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        RaiseTexts();
    }

    private async Task RunCheckAsync(int generation)
    {
        AgentProbe probe;
        try
        {
            probe = await _host.ProbeAsync(AgentId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogWarning(e, "Agent {Agent}: the check failed", AgentId);
            probe = AgentProbe.Missing;
        }

        if (generation != _generation)
        {
            return;
        }

        Apply(probe);
        await CheckReleaseAsync(generation);
    }

    /// <summary>The release an install would get: its size before installing, a newer version of the app's copy as <c>Update to X</c>.</summary>
    private async Task CheckReleaseAsync(int generation)
    {
        if (_installation is { Source: not AgentSource.Managed })
        {
            SetUpdate(null);
            return;
        }

        AgentReleaseLookup lookup;
        try
        {
            lookup = await _host.FindReleaseAsync(AgentId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogWarning(e, "Agent {Agent}: looking for the current release failed", AgentId);
            return;
        }

        if (generation != _generation || lookup.Release is not { } release)
        {
            return;
        }

        _releaseSize = release.Size;
        SetUpdate(_installation is { } installed && AgentVersions.IsNewer(release.Version, installed.Version) ? release.Version : null);
        OnPropertyChanged(nameof(Help));
    }

    private void Apply(AgentProbe probe)
    {
        _installation = probe.Installation;
        _auth = probe.Auth;
        var state = AgentRowStates.From(probe, WasSignedIn(Settings));
        if (state == AgentRowState.Ready)
        {
            RememberSignedIn(true);
        }
        else if (state == AgentRowState.NotInstalled)
        {
            RememberSignedIn(false);
        }

        SetState(state);
    }

    /// <summary><c>Install</c> or <c>Update to X</c>: the official build into the app's folder, then a fresh check.</summary>
    private async Task InstallAsync()
    {
        var updating = _installation is not null;
        var generation = ++_generation;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        Error = null;
        _percent = 0;
        _stage = AgentInstallStage.Resolving;
        SetState(updating ? AgentRowState.Updating : AgentRowState.Installing);
        try
        {
            var result = await _host.InstallAsync(AgentId, new UiProgress(this, generation), cancel.Token);
            if (!result.Succeeded)
            {
                Error = InstallError(result);
            }
        }
        catch (OperationCanceledException)
        {
            Services.Logger.LogInformation("Agent {Agent}: install cancelled", AgentId);
        }
        catch (Exception e)
        {
            Services.Logger.LogError(e, "Agent {Agent}: install failed", AgentId);
            Error = Localizer.Format(nameof(Strings.Settings_Agent_Error_Install), Info.Name, e.Message);
        }
        finally
        {
            _cancel = null;
        }

        await AfterActionAsync();
    }

    /// <summary><c>Sign in</c>: the agent's own login in the browser, waited for (<c>Cancel</c> stops it).</summary>
    private async Task SignInAsync()
    {
        if (_installation is not { } installation)
        {
            return;
        }

        ++_generation;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        Error = null;
        SetState(AgentRowState.SigningIn);
        AgentSignInResult result;
        try
        {
            result = await _host.SignInAsync(installation, cancel.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogError(e, "Agent {Agent}: sign-in failed", AgentId);
            result = new AgentSignInResult(AgentSignInOutcome.StartFailed, null, e.Message);
        }
        catch (OperationCanceledException)
        {
            result = new AgentSignInResult(AgentSignInOutcome.Cancelled, null);
        }
        finally
        {
            _cancel = null;
        }

        Services.Logger.LogInformation("Agent {Agent}: sign-in {Outcome}", AgentId, result.Outcome);
        Error = result.Outcome switch
        {
            AgentSignInOutcome.Exited => Localizer[nameof(Strings.Settings_Agent_Error_SignInExited)],
            AgentSignInOutcome.TimedOut => Localizer[nameof(Strings.Settings_Agent_Error_SignInTimedOut)],
            AgentSignInOutcome.StartFailed => Localizer.Format(nameof(Strings.Settings_Agent_Error_SignInStart), result.Detail),
            _ => null,
        };
        var status = result.IsSignedIn ? result.Status ?? new AgentAuthStatus(AgentAuthState.SignedIn) : result.Status ?? _auth;
        Apply(new AgentProbe(installation, true, status));
        await CheckReleaseAsync(_generation);
    }

    /// <summary><c>Sign out</c>: the agent's own logout; the agent is no longer remembered as signed in, so it isn't "expired".</summary>
    private async Task SignOutAsync()
    {
        if (_installation is not { } installation)
        {
            return;
        }

        ++_generation;
        Error = null;
        SetState(AgentRowState.Checking);
        AgentAuthStatus status;
        try
        {
            status = await _host.SignOutAsync(installation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogError(e, "Agent {Agent}: sign-out failed", AgentId);
            status = new AgentAuthStatus(AgentAuthState.Unknown, Detail: e.Message);
        }

        Services.Logger.LogInformation("Agent {Agent}: signed out, now {State}", AgentId, status.State);
        RememberSignedIn(false);
        if (status.State == AgentAuthState.SignedIn)
        {
            Error = Localizer[nameof(Strings.Settings_Agent_Error_SignOut)];
        }

        Apply(new AgentProbe(installation, true, status));
        await CheckReleaseAsync(_generation);
    }

    private async Task AfterActionAsync()
    {
        _check = null;
        var generation = ++_generation;
        await RunCheckAsync(generation);
    }

    private string InstallError(AgentInstallResult result) => result.Problem switch
    {
        AgentInstallProblem.Offline or AgentInstallProblem.Timeout => Localizer.Format(nameof(Strings.Settings_Agent_Error_Offline), Info.Vendor),
        AgentInstallProblem.HttpError => Localizer.Format(nameof(Strings.Settings_Agent_Error_Server), Info.Vendor),
        AgentInstallProblem.NoBuild => Localizer.Format(nameof(Strings.Settings_Agent_Error_NoBuild), Info.Name),
        AgentInstallProblem.BadSignature or AgentInstallProblem.HashMismatch or AgentInstallProblem.InvalidMetadata =>
            Localizer[nameof(Strings.Settings_Agent_Error_Check)],
        AgentInstallProblem.DiskError => Localizer.Format(nameof(Strings.Settings_Agent_Error_Disk), result.Detail),
        AgentInstallProblem.Busy => Localizer.Format(nameof(Strings.Settings_Agent_Error_Busy), Info.Name),
        _ => Localizer.Format(nameof(Strings.Settings_Agent_Error_Install), Info.Name, result.Detail),
    };

    private bool WasSignedIn(AppSettings settings) => settings.SignedInAgents?.Contains(AgentId, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Keeps <see cref="AppSettings.SignedInAgents"/> in step (written only when it changes).</summary>
    private void RememberSignedIn(bool signedIn)
    {
        if (WasSignedIn(Settings) == signedIn)
        {
            return;
        }

        Save(s =>
        {
            var others = (s.SignedInAgents ?? []).Where(id => !string.Equals(id, AgentId, StringComparison.OrdinalIgnoreCase));
            List<string> list = signedIn ? [.. others, AgentId] : [.. others];
            return s with { SignedInAgents = list.Count == 0 ? null : list };
        });
    }

    private void SetUpdate(string? version)
    {
        if (_updateVersion == version)
        {
            return;
        }

        _updateVersion = version;
        OnPropertyChanged(nameof(UpdateVersion));
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(CanUpdate));
        UpdateCommand.NotifyCanExecuteChanged();
    }

    private void SetState(AgentRowState state)
    {
        var changed = _state != state;
        _state = state;
        RaiseTexts();
        foreach (var name in (string[])[nameof(State), nameof(IsReady), nameof(CanInstall), nameof(CanUpdate), nameof(CanSignIn), nameof(CanSignOut),
                     nameof(CanCheckAgain), nameof(CanCancel), nameof(HasProgress), nameof(IsStatusOk), nameof(IsStatusWarn), nameof(IsStatusBusy), nameof(IsStatusAccent), nameof(IsStatusTinted)])
        {
            OnPropertyChanged(name);
        }

        InstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(SignInText));
        OnPropertyChanged(nameof(SignOutText));
        OnPropertyChanged(nameof(CheckAgainText));
        OnPropertyChanged(nameof(CancelText));
    }

    private void OnProgress(int generation, AgentInstallProgress progress)
    {
        if (generation != _generation || !HasProgress)
        {
            return;
        }

        _stage = progress.Stage;
        _percent = progress.Stage switch
        {
            AgentInstallStage.Downloading when progress.Total > 0 => (int)Math.Clamp(progress.Done * 100 / progress.Total, 0, 100),
            AgentInstallStage.Unpacking => 100,
            _ => _percent,
        };
        RaiseTexts();
    }

    /// <summary>Install progress from the installer's thread, shown on the UI thread.</summary>
    private sealed class UiProgress(AgentRow row, int generation) : IProgress<AgentInstallProgress>
    {
        public void Report(AgentInstallProgress value) => row._ui.Post(() => row.OnProgress(generation, value));
    }
}
