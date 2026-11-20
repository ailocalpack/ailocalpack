using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>One agent or model in the <em>Default agent</em> choice: selectable only while it is ready.</summary>
public sealed class AgentChoice(string agentId, Func<string> label, Func<bool> isReady) : ObservableObject
{
    public AgentChoice(AgentRow agent)
        : this(agent.AgentId, () => agent.Label, () => agent.IsReady)
    {
    }

    public AgentChoice(LlmRow model)
        : this(model.AgentId, () => model.Label, () => model.IsReady)
    {
    }

    public string AgentId { get; } = agentId;

    public string Display => label();

    public bool IsEnabled => isReady();

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Display));
    }

    public override string ToString() => Display;
}

/// <summary>
/// <em>Default agent</em>: the agents and the player's own models, only ready ones selectable, preselected in the
/// generation form: a segmented control while there are at most <see cref="MaxSegments"/>, else a dropdown. The choice is <see cref="AppSettings.DefaultAgent"/>; while that agent isn't ready the first ready one is used
/// (<see cref="Resolve"/>). No ready agent → "Install and sign in to an agent first" instead of the control.
/// </summary>
public sealed class DefaultAgentRow : SettingRow
{
    private AgentChoice? _selected;

    public const int MaxSegments = 3;

    private readonly IReadOnlyList<AgentRow> _agents;

    public DefaultAgentRow(SettingRowServices services, IReadOnlyList<AgentRow> agents)
        : base(services, nameof(Strings.Settings_DefaultAgent_Label), nameof(Strings.Settings_DefaultAgent_Keywords))
    {
        ArgumentNullException.ThrowIfNull(agents);
        _agents = agents;
        SetModels([]);
    }

    public System.Collections.ObjectModel.ObservableCollection<AgentChoice> Choices { get; } = [];

    /// <summary>Few choices: the segmented control; more: the dropdown.</summary>
    public bool IsSegmented => Choices.Count <= MaxSegments;

    public bool ShowSegments => HasReadyAgent && IsSegmented;

    public bool ShowDropdown => HasReadyAgent && !IsSegmented;

    /// <summary>The choices are the agents, then <paramref name="models"/> (UI thread).</summary>
    public void SetModels(IEnumerable<LlmRow> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _selected = null;
        Choices.Clear();
        foreach (var choice in _agents.Select(a => new AgentChoice(a)).Concat(models.Select(m => new AgentChoice(m))))
        {
            Choices.Add(choice);
        }

        OnPropertyChanged(nameof(IsSegmented));
        Sync();
    }

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

            if (!string.Equals(Settings.DefaultAgent, value.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                Save(s => s with { DefaultAgent = value.AgentId });
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

        var id = Resolve(Settings.DefaultAgent, Choices.Where(c => c.IsEnabled).Select(c => c.AgentId));
        SetProperty(ref _selected, Choices.FirstOrDefault(c => c.AgentId == id), nameof(SelectedChoice));
        OnPropertyChanged(nameof(HasReadyAgent));
        OnPropertyChanged(nameof(ShowSegments));
        OnPropertyChanged(nameof(ShowDropdown));
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
