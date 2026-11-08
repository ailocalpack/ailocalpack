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
/// the player's own subscription), one <see cref="AgentRow"/> per agent, <em>Default agent</em>, and <em>Own API key or
/// local model</em> as coming soon. An expired session is an amber alert with <c>Sign in</c> and counts in the badge; a
/// missing, old or never signed-in agent never does. The agents are checked when the section is created (Settings are
/// preloaded at start-up) and again each time it is opened.
/// </summary>
public sealed class AgentsSettingsSection : SettingsSection
{
    private readonly ILogger<AgentsSettingsSection> _logger;

    public AgentsSettingsSection(IAgentHost host, SettingsStore store, Localizer localizer, IUiDispatcher ui, ILogger<AgentsSettingsSection> logger)
        : base(SettingsSectionId.Agents, "IconBot", nameof(Strings.Settings_Agents_Title), nameof(Strings.Settings_Agents_Description), localizer)
    {
        ArgumentNullException.ThrowIfNull(host);
        _logger = logger;
        var services = new SettingRowServices(store, localizer, logger);
        Agents = [.. host.AgentIds.Select(id => new AgentRow(services, host, ui, AgentInfo.Of(id)))];
        DefaultAgent = new DefaultAgentRow(services, Agents);
        OwnKey = new ComingSoonSettingRow(
            services,
            nameof(Strings.Settings_OwnKey_Label),
            nameof(Strings.Settings_OwnKey_Help),
            nameof(Strings.Settings_OwnKey_Keywords));
        Groups =
        [
            new SettingsGroup(services, nameof(Strings.Settings_Group_Agents), Agents),
            new SettingsGroup(services, nameof(Strings.Settings_Group_DefaultAgent), [DefaultAgent]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Later), [OwnKey]),
        ];

        foreach (var agent in Agents)
        {
            agent.StateChanged += (_, _) => Sync();
        }

        Sync();
        _ = CheckAllAsync();
    }

    public IReadOnlyList<AgentRow> Agents { get; }

    public DefaultAgentRow DefaultAgent { get; }

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
        DefaultAgent.Sync();
    }
}
