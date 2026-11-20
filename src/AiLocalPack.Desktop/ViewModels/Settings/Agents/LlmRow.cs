using AiLocalPack.Agents.Llm;
using AiLocalPack.Core.Llm;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>What a row of the player's own model shows.</summary>
public enum LlmRowState
{
    Checking,
    Ready,

    /// <summary>Nothing answers at the address (Ollama / LM Studio not started, offline).</summary>
    Unreachable,

    /// <summary>The server refused the key, or the preset needs one and none is saved.</summary>
    KeyProblem,

    /// <summary>The server's list hasn't the model (removed, or not pulled yet).</summary>
    ModelMissing,

    /// <summary>Another answer: the server's words in the help line.</summary>
    Failed,
}

/// <summary>
/// One of the player's own models in Settings → AI agents → <em>Your models</em> (and, through
/// <see cref="GamePage.GenerationModelCard"/>, on the start form): the preset's mark, the name, a status badge, "model ·
/// host" in mono with where the text goes (this computer / the server), <c>Edit</c>, <c>Remove</c>, and
/// <c>Check again</c> while there is a problem. Checked by listing the server's models (no tokens spent) when made, when
/// the section opens and after an edit; a model the server doesn't list is a warning, not a block, since some servers list
/// only part of what they serve.
/// </summary>
public sealed class LlmRow : SettingRow
{
    private readonly ILlmHost _host;
    private readonly Action<LlmRow> _edit;
    private readonly Action<LlmRow> _remove;
    private Task? _check;
    private int _generation;
    private string? _detail;

    public LlmRow(SettingRowServices services, ILlmHost host, LlmConnection connection, Action<LlmRow> edit, Action<LlmRow> remove)
        : base(services, nameof(Strings.Settings_Llm_Label), nameof(Strings.Settings_Llm_Keywords))
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(connection);
        _host = host;
        _edit = edit;
        _remove = remove;
        Connection = connection;
        EditCommand = new RelayCommand(() => _edit(this));
        RemoveCommand = new RelayCommand(() => _remove(this));
        CheckAgainCommand = new AsyncRelayCommand(CheckAsync);
    }

    /// <summary>Raised on the UI thread when <see cref="State"/> changed (the default agent, the start form).</summary>
    public event EventHandler? StateChanged;

    public LlmConnection Connection { get; private set; }

    public AgentInfo Info => AgentInfo.ForLlm(Connection);

    public string AgentId => Connection.Id;

    public LlmRowState State { get; private set; } = LlmRowState.Checking;

    public bool IsReady => State is LlmRowState.Ready or LlmRowState.ModelMissing;

    public override string Label => Connection.Name;

    public string StatusText => State switch
    {
        LlmRowState.Checking => Localizer[nameof(Strings.Settings_Llm_Status_Checking)],
        LlmRowState.Ready => Localizer[nameof(Strings.Settings_Llm_Status_Ready)],
        LlmRowState.Unreachable => Localizer[nameof(Strings.Settings_Llm_Status_Unreachable)],
        LlmRowState.KeyProblem => Localizer[nameof(Strings.Settings_Llm_Status_Key)],
        LlmRowState.ModelMissing => Localizer[nameof(Strings.Settings_Llm_Status_ModelMissing)],
        _ => Localizer[nameof(Strings.Settings_Llm_Status_Failed)],
    };

    public bool IsStatusOk => State == LlmRowState.Ready;

    public bool IsStatusWarn => State is LlmRowState.Unreachable or LlmRowState.KeyProblem or LlmRowState.ModelMissing or LlmRowState.Failed;

    public bool IsStatusBusy => State == LlmRowState.Checking;

    /// <summary>"qwen3:30b · localhost:11434".</summary>
    public string SourceText => $"{Connection.Model} · {Connection.Host}";

    /// <summary>Where the game's text goes: "Runs on this computer" / "Text is sent to openrouter.ai".</summary>
    public string PrivacyText => Connection.IsLocal
        ? Localizer[nameof(Strings.Settings_Llm_Local)]
        : Localizer.Format(nameof(Strings.Settings_Llm_Remote), Connection.Host);

    public bool IsLocal => Connection.IsLocal;

    /// <summary>What to do about a problem, with the server's words when they help.</summary>
    public override string? Help => State switch
    {
        LlmRowState.Unreachable => Connection.Provider switch
        {
            "ollama" => Localizer[nameof(Strings.Settings_Llm_Help_StartOllama)],
            "lmstudio" => Localizer[nameof(Strings.Settings_Llm_Help_StartLmStudio)],
            _ => Localizer.Format(nameof(Strings.Settings_Llm_Help_Unreachable), Connection.Host),
        },
        LlmRowState.KeyProblem => Localizer[nameof(Strings.Settings_Llm_Help_Key)],
        LlmRowState.ModelMissing => Localizer.Format(nameof(Strings.Settings_Llm_Help_ModelMissing), Connection.Model),
        LlmRowState.Failed => _detail,
        _ => null,
    };

    public bool HasHelp => Help is not null;

    public IRelayCommand EditCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public IAsyncRelayCommand CheckAgainCommand { get; }

    public bool CanCheckAgain => State is not (LlmRowState.Ready or LlmRowState.Checking);

    public string EditText => Localizer[nameof(Strings.Settings_Llm_Edit)];

    public string RemoveText => Localizer[nameof(Strings.Settings_Llm_Remove)];

    public string CheckAgainText => Localizer[nameof(Strings.Settings_Agent_CheckAgain)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Connection.Model, Connection.Host, Info.Vendor];

    /// <summary>The settings changed this model (UI thread): shown at once and checked again.</summary>
    public void Update(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection == Connection)
        {
            return;
        }

        Connection = connection;
        RaiseTexts();
        _generation++;
        _check = null;
        _ = CheckAsync();
    }

    /// <summary>Lists the server's models; a check already running is shared. UI thread.</summary>
    public Task CheckAsync()
    {
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
        if (State != LlmRowState.Ready)
        {
            SetState(LlmRowState.Checking, null);
        }

        LlmCheck check;
        try
        {
            check = await _host.ProbeAsync(Connection);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Services.Logger.LogWarning(e, "Model {Id}: the check failed", AgentId);
            check = new LlmCheck(LlmCheckState.Failed, [], e.Message);
        }

        if (generation != _generation)
        {
            return;
        }

        SetState(
            check.State switch
            {
                LlmCheckState.Ready or LlmCheckState.NoToolCalls => LlmRowState.Ready,
                LlmCheckState.ModelMissing => LlmRowState.ModelMissing,
                LlmCheckState.Unreachable => LlmRowState.Unreachable,
                LlmCheckState.KeyRefused => LlmRowState.KeyProblem,
                _ => LlmRowState.Failed,
            },
            check.Detail);
    }

    private void SetState(LlmRowState state, string? detail)
    {
        // A preset that needs a key and has none is a key problem whatever the list said (OpenRouter lists models without one).
        if (state is LlmRowState.Ready or LlmRowState.ModelMissing && LlmProviders.Of(Connection.Provider).NeedsKey && !Connection.HasKey)
        {
            state = LlmRowState.KeyProblem;
        }

        var changed = State != state;
        State = state;
        _detail = detail;
        RaiseTexts();
        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseTexts()
    {
        foreach (var name in (string[])[nameof(State), nameof(IsReady), nameof(Label), nameof(Info), nameof(StatusText), nameof(IsStatusOk), nameof(IsStatusWarn),
            nameof(IsStatusBusy), nameof(SourceText), nameof(PrivacyText), nameof(IsLocal), nameof(Help), nameof(HasHelp), nameof(CanCheckAgain),
            nameof(EditText), nameof(RemoveText), nameof(CheckAgainText)])
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>
/// <em>Your models</em> before the first one: what they are (a local model or an API key), and <c>Add model</c>.
/// </summary>
public sealed class LlmEmptyRow(SettingRowServices services, IRelayCommand add)
    : SettingRow(services, nameof(Strings.Settings_Llm_Empty_Title), nameof(Strings.Settings_Llm_Keywords))
{
    public IRelayCommand AddCommand { get; } = add;

    public override string? Help => Localizer[nameof(Strings.Settings_Llm_Empty_Text)];

    public string AddText => Localizer[nameof(Strings.Settings_Llm_Add)];

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(AddText));
    }
}

/// <summary>The last row of <em>Your models</em> once there is one: <c>+ Add model</c>.</summary>
public sealed class LlmAddRow(SettingRowServices services, IRelayCommand add)
    : SettingRow(services, nameof(Strings.Settings_Llm_Add), nameof(Strings.Settings_Llm_Keywords))
{
    public IRelayCommand AddCommand { get; } = add;
}
