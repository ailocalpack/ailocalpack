using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>One agent in the <em>Default agent</em> segmented control: selectable only while it is ready.</summary>
public sealed class AgentChoice(AgentRow agent) : ObservableObject
{
    public AgentRow Agent { get; } = agent;

    public string Display => Agent.Label;

    public bool IsEnabled => Agent.IsReady;

    internal void Refresh() => OnPropertyChanged(nameof(IsEnabled));

    public override string ToString() => Display;
}

/// <summary>
/// <em>Default agent</em>: a segmented control of the agents, only ready ones selectable, preselected in the generation
/// form. The choice is <see cref="AppSettings.DefaultAgent"/>; while that agent isn't ready the first ready one is used
/// (<see cref="Resolve"/>). No ready agent → "Install and sign in to an agent first" instead of the control.
/// </summary>
public sealed class DefaultAgentRow : SettingRow
{
    private AgentChoice? _selected;

    public DefaultAgentRow(SettingRowServices services, IReadOnlyList<AgentRow> agents)
        : base(services, nameof(Strings.Settings_DefaultAgent_Label), nameof(Strings.Settings_DefaultAgent_Keywords))
    {
        ArgumentNullException.ThrowIfNull(agents);
        Choices = [.. agents.Select(a => new AgentChoice(a))];
        Sync();
    }

    public IReadOnlyList<AgentChoice> Choices { get; }

    public bool HasReadyAgent => Choices.Any(c => c.IsEnabled);

    public string NoAgentText => Localizer[nameof(Strings.Settings_DefaultAgent_None)];

    /// <summary>The agent used by default; picking one stores it.</summary>
    public AgentChoice? SelectedChoice
    {
        get => _selected;
        set
        {
            // The segmented control pushes null while it rebuilds, and a disabled item can't be a choice.
            if (value is null || !value.IsEnabled || !SetProperty(ref _selected, value))
            {
                return;
            }

            if (!string.Equals(Settings.DefaultAgent, value.Agent.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                Save(s => s with { DefaultAgent = value.Agent.AgentId });
            }
        }
    }

    /// <summary>The default agent's ID: the stored one while it is ready, else the first ready one, else <c>null</c>.</summary>
    public static string? Resolve(string? setting, IEnumerable<string> readyAgents)
    {
        ArgumentNullException.ThrowIfNull(readyAgents);
        var ready = readyAgents.ToList();
        return ready.FirstOrDefault(id => string.Equals(id, setting, StringComparison.OrdinalIgnoreCase)) ?? ready.FirstOrDefault();
    }

    /// <summary>Follows the agents' states and the stored choice (UI thread).</summary>
    public void Sync()
    {
        foreach (var choice in Choices)
        {
            choice.Refresh();
        }

        var id = Resolve(Settings.DefaultAgent, Choices.Where(c => c.IsEnabled).Select(c => c.Agent.AgentId));
        SetProperty(ref _selected, Choices.FirstOrDefault(c => c.Agent.AgentId == id), nameof(SelectedChoice));
        OnPropertyChanged(nameof(HasReadyAgent));
    }

    protected override string? HelpKey(AppSettings settings) => nameof(Strings.Settings_DefaultAgent_Help);

    protected override void OnSettingsChanged(AppSettings settings)
    {
        Sync();
        base.OnSettingsChanged(settings);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(NoAgentText));
    }
}
