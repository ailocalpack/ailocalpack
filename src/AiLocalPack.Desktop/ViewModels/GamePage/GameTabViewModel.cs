using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>The game page's tabs (design §3).</summary>
public enum GamePageTab
{
    /// <summary>Pick by the rule: Generation while a job exists, otherwise Packs.</summary>
    Default,
    Packs,
    Generation,

    /// <summary>Coming soon (phase 2): opens the illustration.</summary>
    Audio,

    /// <summary>Coming soon (phase 3): opens the illustration.</summary>
    Textures,
}

/// <summary>One tab of the bar: its label, the counter after it (pack count, generation %), the "soon" pill.</summary>
public sealed partial class GameTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>"3" on Packs, "42%" on Generation while a job runs; empty = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCounter))]
    private string _counter = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The "soon" pill text.</summary>
    [ObservableProperty]
    private string _soonText = string.Empty;

    public GameTabViewModel(GamePageTab tab, Action<GamePageTab> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Tab = tab;
        SelectCommand = new RelayCommand(() => select(tab));
    }

    public GamePageTab Tab { get; }

    /// <summary>Audio and Textures: muted with a "soon" pill, yet selectable (they show the coming-soon illustration).</summary>
    public bool IsSoon => Tab is GamePageTab.Audio or GamePageTab.Textures;

    public bool HasCounter => Counter.Length > 0;

    /// <summary>The generation % is teal, the pack count plain.</summary>
    public bool IsGenCounter => Tab == GamePageTab.Generation;

    public IRelayCommand SelectCommand { get; }
}
