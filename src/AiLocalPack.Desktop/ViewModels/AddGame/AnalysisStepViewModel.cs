using AiLocalPack.Desktop.Localization;
using AiLocalPack.Toolkit.Engines;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.AddGame;

/// <summary>Screens of the add-folder dialog (design §2).</summary>
public enum AddGameStep
{
    /// <summary>Step 1: drop zone, "Choose folder…", hint; shows an error when the last folder had no game.</summary>
    Pick,

    /// <summary>Spinner and checklist while the folder is searched and the game fingerprinted.</summary>
    Analyzing,

    /// <summary>Step 2: one game's summary (cover, name, facts, answer block, details, primary action).</summary>
    Summary,

    /// <summary>Several games: folders scanned, games streaming in, <c>Stop and show</c>; then each game is analyzed.</summary>
    Scanning,

    /// <summary>Several games: count, status tally, cover strip, skipped folders, <c>Start adding</c>.</summary>
    Overview,

    /// <summary>Several games: one game's summary at a time with a segmented progress bar, Previous / Skip / Add.</summary>
    Review,

    /// <summary>Several games: what was added and what was skipped, <c>To library</c>.</summary>
    Result,
}

public enum AnalysisStepState
{
    Waiting,
    Running,
    Done,
}

/// <summary>One line of the "Analyzing" checklist: executables, engine, fingerprint, registry lookup.</summary>
public sealed partial class AnalysisStepViewModel(string titleKey, Localizer localizer) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsRunning), nameof(IsDone))]
    private AnalysisStepState _state;

    /// <summary>Result or progress on the right ("Ren'Py", "812 of 1,248 files").</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    public string Title => localizer[titleKey];

    public bool IsWaiting => State == AnalysisStepState.Waiting;

    public bool IsRunning => State == AnalysisStepState.Running;

    public bool IsDone => State == AnalysisStepState.Done;

    public void Set(AnalysisStepState state, string? detail = null)
    {
        State = state;
        if (detail is not null)
        {
            Detail = detail;
        }
    }

    public void RefreshTexts() => OnPropertyChanged(nameof(Title));
}

/// <summary>An engine the player can choose in Edit game details; <see cref="Display"/> is shown in the dropdown.</summary>
public sealed record EngineOption(EngineKind Kind, string Display)
{
    public override string ToString() => Display;
}
