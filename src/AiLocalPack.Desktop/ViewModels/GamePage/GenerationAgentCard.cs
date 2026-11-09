using System.ComponentModel;
using AiLocalPack.Agents;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>
/// One agent card of the start form (design §6a): a radio choice with the agent's status and its inline fix. It shows the
/// same <see cref="AgentRow"/> as Settings → AI agents, so an install or sign-in started here shows there and back:
/// <em>Not installed</em> → <c>Install</c> (download progress inline, <c>Cancel</c>); <em>Not signed in</em> →
/// <c>Sign in</c> ("Finish signing in in your browser…", <c>Cancel</c>); <em>Ready</em> with the source and version.
/// </summary>
public sealed partial class GenerationAgentCard : ObservableObject
{
    private readonly Localizer _localizer;

    [ObservableProperty]
    private bool _isSelected;

    public GenerationAgentCard(AgentRow row, Localizer localizer, Action<GenerationAgentCard> select)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(select);
        Row = row;
        _localizer = localizer;
        SelectCommand = new RelayCommand(() => select(this));
        row.PropertyChanged += OnRowChanged;
    }

    /// <summary>Raised when the agent's state changed (the form recomputes its checks and the start rule).</summary>
    public event EventHandler? StateChanged;

    public AgentRow Row { get; }

    public AgentInfo Info => Row.Info;

    public string AgentId => Row.AgentId;

    public string Name => Row.Info.Name;

    public IRelayCommand SelectCommand { get; }

    public bool IsReady => Row.IsReady;

    /// <summary>"Ready", "Not installed", "Not signed in", "Downloading the official build · 40%", "Finish signing in in your browser…".</summary>
    public string StatusText => Row.State switch
    {
        AgentRowState.Ready => _localizer[nameof(Strings.Gen_Agent_Ready)],
        AgentRowState.NotInstalled => _localizer[nameof(Strings.Gen_Agent_NotInstalled)],
        AgentRowState.NotSignedIn or AgentRowState.SessionExpired => _localizer[nameof(Strings.Gen_Agent_NotSignedIn)],
        AgentRowState.Installing or AgentRowState.Updating => _localizer.Format(nameof(Strings.Gen_Agent_Installing), Row.Percent),
        AgentRowState.SigningIn => _localizer[nameof(Strings.Gen_Agent_SigningIn)],
        _ => Row.StatusText,
    };

    public bool IsStatusOk => Row.State == AgentRowState.Ready;

    /// <summary>Missing, signed out, too old or unknown: the player has a step to take (red icon).</summary>
    public bool IsStatusBad => Row.State is AgentRowState.NotInstalled or AgentRowState.NotSignedIn or AgentRowState.SessionExpired
        or AgentRowState.TooOld or AgentRowState.SignInUnknown;

    public bool IsStatusBusy => AgentRowStates.IsBusy(Row.State);

    /// <summary>"on PATH · v2.1.319" / "installed by ailocalpack · v2.1.311", for a ready agent.</summary>
    public string SourceText
    {
        get
        {
            if (!IsReady || Row.Installation is not { } installation)
            {
                return string.Empty;
            }

            var source = _localizer[installation.Source == AgentSource.Managed ? nameof(Strings.Gen_Agent_Source_Managed) : nameof(Strings.Gen_Agent_Source_Own)];
            return installation.Version is { } version ? $"{source} · v{version}" : source;
        }
    }

    public bool HasSource => SourceText.Length > 0;

    /// <summary>What to do about a too old or unknown agent (the row's help), else nothing.</summary>
    public string? Hint => Row.State is AgentRowState.TooOld or AgentRowState.SignInUnknown ? Row.Help : null;

    public bool HasHint => Hint is not null;

    public bool HasProgress => Row.HasProgress;

    public int Percent => Row.Percent;

    internal void RefreshTexts()
    {
        foreach (var name in (string[])[nameof(StatusText), nameof(SourceText), nameof(HasSource), nameof(Hint), nameof(HasHint)])
        {
            OnPropertyChanged(name);
        }
    }

    internal void Close() => Row.PropertyChanged -= OnRowChanged;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTexts();
        foreach (var name in (string[])[nameof(IsReady), nameof(IsStatusOk), nameof(IsStatusBad), nameof(IsStatusBusy), nameof(HasProgress), nameof(Percent)])
        {
            OnPropertyChanged(name);
        }

        if (e.PropertyName == nameof(AgentRow.State))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
