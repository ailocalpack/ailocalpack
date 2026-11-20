using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.Settings.Libraries;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>
/// Settings → AI agents (design §8; task 44): a lead line (agents only generate; installing never needs one; they run on
/// the player's own subscription), one <see cref="AgentRow"/> per agent, <em>Default agent</em>, <em>Chunks translated at once</em> (Translate stage workers), and <em>Own API key or
/// local model</em> as coming soon. An expired session is an amber alert with <c>Sign in</c> and counts in the badge; a
/// missing, old or never signed-in agent never does. The agents are checked when the section is created (Settings are
/// preloaded at start-up) and again each time it is opened.
/// </summary>
public sealed class AgentsSettingsSection : SettingsSection
{
    private readonly AgentUpdates _updates;
    private readonly ILogger<AgentsSettingsSection> _logger;

    public AgentsSettingsSection(IAgentHost host, AgentUpdates updates, SettingsStore store, Localizer localizer, IUiDispatcher ui, ILogger<AgentsSettingsSection> logger)
        : base(SettingsSectionId.Agents, "IconBot", nameof(Strings.Settings_Agents_Title), nameof(Strings.Settings_Agents_Description), localizer)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(ui);
        _updates = updates;
        _logger = logger;
        var services = new SettingRowServices(store, localizer, logger);
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
        OwnKey = new ComingSoonSettingRow(
            services,
            nameof(Strings.Settings_OwnKey_Label),
            nameof(Strings.Settings_OwnKey_Help),
            nameof(Strings.Settings_OwnKey_Keywords));
        Groups =
        [
            new SettingsGroup(services, nameof(Strings.Settings_Group_Agents), Agents),
            new SettingsGroup(services, nameof(Strings.Settings_Group_DefaultAgent), [DefaultAgent]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Translation), [TranslateWorkers]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Later), [OwnKey]),
        ];

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

    public ComingSoonSettingRow OwnKey { get; }

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    public override string? Lead => Localizer[nameof(Strings.Settings_Agents_Lead)];

    /// <summary>Checks every agent again (programs, sign-in, updates); agents in the middle of an action are left alone.</summary>
    public async Task CheckAllAsync()
    {
        try
        {
            await Task.WhenAll(Agents.Select(a => a.CheckAsync()));
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
