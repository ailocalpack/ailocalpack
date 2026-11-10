using Avalonia;
using Avalonia.Controls;

namespace AiLocalPack.Desktop.Views;

/// <summary>An agent's logo tile; its data context is the agent's <see cref="Agents.AgentInfo"/>.</summary>
public partial class AgentLogo : UserControl
{
    public static readonly StyledProperty<bool> IsSmallProperty = AvaloniaProperty.Register<AgentLogo, bool>(nameof(IsSmall));

    public AgentLogo() => InitializeComponent();

    /// <summary>The 28 px tile of the Generation tab's agent cards instead of the 40 px one.</summary>
    public bool IsSmall
    {
        get => GetValue(IsSmallProperty);
        set => SetValue(IsSmallProperty, value);
    }
}
