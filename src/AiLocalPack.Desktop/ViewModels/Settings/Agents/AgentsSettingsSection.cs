using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.Settings.Libraries;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>
/// Settings → AI agents (design §8; task 44): a lead line (agents only generate; installing never needs one; they run on
/// the player's own subscription), one <see cref="AgentRow"/> per agent, <em>Your models</em> (the player's own
/// OpenAI-compatible models: an <see cref="LlmRow"/> each and <c>+ Add model</c>, or <see cref="LlmEmptyRow"/> before the
/// first; <see cref="LlmDialogViewModel"/> adds and edits, <c>Remove</c> confirms), <em>Default agent</em> and
/// <em>Chunks translated at once</em> (Translate stage workers). An expired session is an amber alert with <c>Sign in</c> and counts in the badge; a
/// missing, old or never signed-in agent never does. The agents are checked when the section is created (Settings are
/// preloaded at start-up) and again each time it is opened.
/// </summary>
public sealed class AgentsSettingsSection : SettingsSection
{
    private readonly AgentUpdates _updates;
    private readonly ILogger<AgentsSettingsSection> _logger;
    private readonly SettingRowServices _services;
    private readonly ILlmHost _llms;
    private readonly IDialogService _dialogs;
    private readonly SettingsStore _store;
    private readonly LlmEmptyRow _emptyModels;
    private readonly LlmAddRow _addModel;

    public AgentsSettingsSection(
        IAgentHost host,
        AgentUpdates updates,
        SettingsStore store,
        Localizer localizer,
        IUiDispatcher ui,
        ILlmHost llms,
        IDialogService dialogs,
        ILogger<AgentsSettingsSection> logger)
        : base(SettingsSectionId.Agents, "IconBot", nameof(Strings.Settings_Agents_Title), nameof(Strings.Settings_Agents_Description), localizer)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(ui);
        _updates = updates;
        _logger = logger;
        var services = new SettingRowServices(store, localizer, logger);
        _services = services;
        _llms = llms;
        _dialogs = dialogs;
        _store = store;
        AddModelCommand = new RelayCommand(() => AddModel());
        _emptyModels = new LlmEmptyRow(services, AddModelCommand);
        _addModel = new LlmAddRow(services, AddModelCommand);
        Agents = [.. host.AgentIds.Select(id => new AgentRow(services, host, updates, ui, AgentInfo.Of(id)))];
        DefaultAgent = new DefaultAgentRow(services, Agents);
        TranslateWorkers = new NumberSettingRow(
            services,
            nameof(Strings.Settings_TranslateWorkers_Label),
            nameof(Strings.Settings_TranslateWorkers_Help),
            nameof(Strings.Settings_TranslateWorkers_Keywords),
            AppSettings.MinTranslateWorkers,
            AppSettings.MaxTranslateWorkers,
            s => s.TranslateWorkerCount,
            (s, n) => s with { TranslateWorkers = n });
        Groups =
        [
            new SettingsGroup(services, nameof(Strings.Settings_Group_Agents), Agents),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Models), ModelRows, _ => nameof(Strings.Settings_Llm_Note)),
            new SettingsGroup(services, nameof(Strings.Settings_Group_DefaultAgent), [DefaultAgent]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Translation), [TranslateWorkers]),
        ];
        SyncModels(store.Current);
        store.Changed += (_, s) => ui.Post(() => SyncModels(s));

        foreach (var agent in Agents)
        {
            agent.StateChanged += (_, _) => Sync();
        }

        // A background check (every few hours) found an update or one went: the rows and the list's hint follow.
        updates.Changed += (_, _) => ui.Post(OnUpdatesChanged);
        Sync();
        _ = CheckAllAsync();
    }

    public IReadOnlyList<AgentRow> Agents { get; }

    public DefaultAgentRow DefaultAgent { get; }

    /// <summary><em>Chunks translated at once</em> (<see cref="AppSettings.TranslateWorkers"/>).</summary>
    public NumberSettingRow TranslateWorkers { get; }

    /// <summary>The player's own models, in the order added.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LlmRow> Models { get; } = [];

    /// <summary><em>Your models</em>' rows: the models and <c>+ Add model</c>, or the empty row.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SettingRow> ModelRows { get; } = [];

    /// <summary>Opens <em>Add model</em> (also the start form's add tile).</summary>
    public IRelayCommand AddModelCommand { get; }

    /// <summary>Raised when a model was added or removed (the start form's cards follow).</summary>
    public event EventHandler? ModelsChanged;

    /// <summary>Opens <em>Add model</em>; <paramref name="saved"/> runs with the new model once saved.</summary>
    public void AddModel(Action<LlmConnection>? saved = null) => _dialogs.Show<LlmDialogViewModel>(new LlmDialogRequest(null, saved));

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    public override string? Lead => Localizer[nameof(Strings.Settings_Agents_Lead)];

    /// <summary>Checks every agent again (programs, sign-in, updates); agents in the middle of an action are left alone.</summary>
    public async Task CheckAllAsync()
    {
        try
        {
            await Task.WhenAll(Agents.Select(a => a.CheckAsync()).Concat(Models.Select(m => m.CheckAsync())));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Checking the agents failed");
        }
    }

    public override void OnShown() => _ = CheckAllAsync();

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(Lead));
        Sync();
    }

    /// <summary>Finds the agent's row and runs its <c>Update to X</c> (Activity's <c>Update</c>); nothing when it can't update now.</summary>
    public void Update(string agentId)
    {
        if (Agents.FirstOrDefault(a => string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase)) is { } row && row.UpdateCommand.CanExecute(null))
        {
            row.UpdateCommand.Execute(null);
        }
    }

    /// <summary>The rows follow the settings' models: new ones added and checked, changed ones updated, removed ones dropped (UI thread).</summary>
    private void SyncModels(AppSettings settings)
    {
        AgentInfo.Remember(settings.Llms);
        var wanted = settings.Llms;
        var changed = false;
        foreach (var gone in Models.Where(m => LlmConnections.Find(wanted, m.AgentId) is null).ToList())
        {
            gone.StateChanged -= OnModelStateChanged;
            gone.Detach();
            Models.Remove(gone);
            changed = true;
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var connection = wanted[i];
            if (Models.FirstOrDefault(m => string.Equals(m.AgentId, connection.Id, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                row.Update(connection);
                if (Models.IndexOf(row) != i)
                {
                    Models.Move(Models.IndexOf(row), i);
                }

                continue;
            }

            var added = new LlmRow(_services, _llms, connection, EditModel, RemoveModel);
            added.StateChanged += OnModelStateChanged;
            Models.Insert(i, added);
            _ = added.CheckAsync();
            changed = true;
        }

        if (!changed && ModelRows.Count > 0)
        {
            return;
        }

        ModelRows.Clear();
        foreach (var model in Models)
        {
            ModelRows.Add(model);
        }

        ModelRows.Add(Models.Count == 0 ? _emptyModels : _addModel);
        DefaultAgent.SetModels(Models);
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnModelStateChanged(object? sender, EventArgs e) => DefaultAgent.Sync();

    private void EditModel(LlmRow row) => _dialogs.Show<LlmDialogViewModel>(new LlmDialogRequest(row.Connection));

    /// <summary>Asks first: the model leaves the list and its key the keychain; jobs and translations made with it stay.</summary>
    private void RemoveModel(LlmRow row)
    {
        var connection = row.Connection;
        _dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
            Localizer.Format(nameof(Strings.Settings_Llm_Remove_Title), connection.Name),
            Localizer[nameof(Strings.Settings_Llm_Remove_Text)],
            Localizer[nameof(Strings.Settings_Llm_Remove)],
            () =>
            {
                try
                {
                    _store.Update(s => s.WithoutLlm(connection.Id));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(e, "Model {Id} can't be removed", connection.Id);
                    return;
                }

                if (connection.HasKey)
                {
                    _ = _llms.DeleteKeyAsync(connection.Id);
                }

                _logger.LogInformation("Model {Id} removed", connection.Id);
            }));
    }

    private void OnUpdatesChanged()
    {
        foreach (var agent in Agents)
        {
            agent.ApplyOffer();
        }

        UpdateHint();
    }

    /// <summary>"update" in the section list while the app's copy of an agent has one.</summary>
    private void UpdateHint() =>
        Hint = _updates.Offers.Count > 0 ? Localizer[nameof(Strings.Settings_Agents_Hint_Update)] : null;

    /// <summary>One alert per expired session (<c>Sign in</c>), the badge, and the default agent (UI thread).</summary>
    private void Sync()
    {
        Alerts.Clear();
        foreach (var agent in Agents.Where(a => AgentRowStates.IsWarning(a.State)))
        {
            Alerts.Add(new SettingsAlert(
                Localizer.Format(nameof(Strings.Settings_Alert_AgentExpired_Title), agent.Label),
                Localizer[nameof(Strings.Settings_Alert_AgentExpired_Text)],
                Localizer[nameof(Strings.Settings_Agent_SignIn)],
                agent.SignInCommand));
        }

        BadgeCount = Alerts.Count;
        BadgeIsError = false;
        UpdateHint();
        DefaultAgent.Sync();
    }
}
