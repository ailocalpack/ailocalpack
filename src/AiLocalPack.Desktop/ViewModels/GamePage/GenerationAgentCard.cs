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
public sealed partial class GenerationAgentCard : GenerationAgentChoice
{
    private readonly Localizer _localizer;

    public GenerationAgentCard(AgentRow row, Localizer localizer, Action<GenerationAgentChoice> select)
        : base(select)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        _localizer = localizer;
        row.PropertyChanged += OnRowChanged;
    }

    public AgentRow Row { get; }

    public override AgentInfo Info => Row.Info;

    public override string AgentId => Row.AgentId;

    public override string Name => Row.Info.Name;

    public override bool IsReady => Row.IsReady;

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

    public override bool IsStatusBusy => AgentRowStates.IsBusy(Row.State);

    /// <summary>"on PATH · v2.1.319" / "installed by AI Local Pack · v2.1.311", for a ready agent.</summary>
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

    internal override void RefreshTexts()
    {
        foreach (var name in (string[])[nameof(StatusText), nameof(SourceText), nameof(HasSource), nameof(Hint), nameof(HasHint)])
        {
            OnPropertyChanged(name);
        }
    }

    internal override void Close() => Row.PropertyChanged -= OnRowChanged;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTexts();
        foreach (var name in (string[])[nameof(IsReady), nameof(IsStatusOk), nameof(IsStatusBad), nameof(IsStatusBusy), nameof(HasProgress), nameof(Percent)])
        {
            OnPropertyChanged(name);
        }

        if (e.PropertyName == nameof(AgentRow.State))
        {
            RaiseStateChanged();
        }
    }
}

/// <summary>
/// A choice of the start form's agent grid: an agent CLI (<see cref="GenerationAgentCard"/>) or one of the player's own
/// models (<see cref="GenerationModelCard"/>).
/// </summary>
public abstract partial class GenerationAgentChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    protected GenerationAgentChoice(Action<GenerationAgentChoice> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>Raised when the agent's state changed (the form recomputes its checks and the start rule).</summary>
    public event EventHandler? StateChanged;

    public abstract AgentInfo Info { get; }

    public abstract string AgentId { get; }

    public abstract string Name { get; }

    public abstract bool IsReady { get; }

    public abstract bool IsStatusBusy { get; }

    /// <summary>It can write its own reader for a game the toolkit doesn't read (file and command tools in a sandbox).</summary>
    public virtual bool CanWriteScripts => true;

    /// <summary>It can search the web (a Thorough job's Study stage).</summary>
    public virtual bool CanSearchWeb => true;

    public IRelayCommand SelectCommand { get; }

    internal abstract void RefreshTexts();

    internal abstract void Close();

    protected void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One of the player's own models on the start form: its mark, name, status ("Ready" / "Server not reachable" / "API key
/// problem"…), "model · host" and where the text goes; <c>Check again</c> and <c>Edit</c> for a problem. It shows the
/// same <see cref="LlmRow"/> as Settings → AI agents.
/// </summary>
public sealed class GenerationModelCard : GenerationAgentChoice
{
    public GenerationModelCard(LlmRow row, Action<GenerationAgentChoice> select)
        : base(select)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        row.PropertyChanged += OnRowChanged;
    }

    public LlmRow Row { get; }

    public override AgentInfo Info => Row.Info;

    public override string AgentId => Row.AgentId;

    public override string Name => Row.Label;

    public override bool IsReady => Row.IsReady;

    public override bool IsStatusBusy => Row.IsStatusBusy;

    public override bool CanWriteScripts => false;

    public override bool CanSearchWeb => false;

    internal override void RefreshTexts()
    {
    }

    internal override void Close() => Row.PropertyChanged -= OnRowChanged;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LlmRow.Label) or nameof(LlmRow.Info))
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Info));
        }

        if (e.PropertyName == nameof(LlmRow.State))
        {
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsStatusBusy));
            RaiseStateChanged();
        }
    }
}

/// <summary>The start form's last tile before the player has a model: "Your own model · Ollama, LM Studio or an API key" + <c>Add</c>.</summary>
public sealed class GenerationAddModelTile(IRelayCommand add)
{
    public IRelayCommand AddCommand { get; } = add;
}
