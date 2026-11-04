using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.Startup;

/// <summary>When a startup step runs (design §9).</summary>
public enum StartupPhase
{
    /// <summary>Before the library opens: the step touches what the library shows.</summary>
    Blocking,

    /// <summary>While the player already uses the library.</summary>
    Background,
}

public enum StartupStepStatus
{
    Waiting,
    Running,
    Done,
    Skipped,
}

/// <summary>How a finished step ended.</summary>
public enum StartupTone
{
    Ok,

    /// <summary>Finished, but something the player should know about (damaged file, failure).</summary>
    Warn,
}

/// <summary>
/// One line of the startup checklist: label, status and the result line ("18 games"). Changed on the UI thread only;
/// texts are factories so they follow a UI language switch.
/// </summary>
public sealed partial class StartupStep : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly string _labelKey;
    private readonly TimeProvider _time;
    private Func<string>? _detail;

    public StartupStep(string id, StartupPhase phase, string labelKey, Localizer localizer, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelKey);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(time);
        Id = id;
        Phase = phase;
        _labelKey = labelKey;
        _localizer = localizer;
        _time = time;
        _localizer.CultureChanged += (_, _) => RefreshTexts();
    }

    public string Id { get; }

    public StartupPhase Phase { get; }

    public string Label => _localizer[_labelKey];

    /// <summary>The line under the label: "Waiting", the progress or the result; empty while running without progress.</summary>
    public string Detail => Status == StartupStepStatus.Waiting ? _localizer[nameof(Strings.Startup_Waiting)] : _detail?.Invoke() ?? string.Empty;

    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsRunning), nameof(IsDone), nameof(IsSkipped), nameof(IsFinished), nameof(IsOk), nameof(IsWarn), nameof(Detail), nameof(HasDetail))]
    private StartupStepStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsWarn))]
    private StartupTone _tone;

    /// <summary>When the step started running; <c>null</c> while waiting.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    public bool IsWaiting => Status == StartupStepStatus.Waiting;

    public bool IsRunning => Status == StartupStepStatus.Running;

    public bool IsDone => Status == StartupStepStatus.Done;

    public bool IsSkipped => Status == StartupStepStatus.Skipped;

    /// <summary>Done or skipped.</summary>
    public bool IsFinished => Status is StartupStepStatus.Done or StartupStepStatus.Skipped;

    public bool IsOk => IsDone && Tone == StartupTone.Ok;

    public bool IsWarn => IsDone && Tone == StartupTone.Warn;

    /// <summary>Starts the step; <paramref name="detail"/> is the progress line, if any.</summary>
    public void Start(Func<string>? detail = null)
    {
        StartedAt = _time.GetUtcNow();
        _detail = detail;
        Status = StartupStepStatus.Running;
        RefreshTexts();
    }

    public void Complete(StartupTone tone, Func<string> detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        _detail = detail;
        Tone = tone;
        Status = StartupStepStatus.Done;
        RefreshTexts();
    }

    /// <summary>Nothing to do this time (no saved games yet, no new games); <paramref name="reason"/> says why.</summary>
    public void Skip(Func<string> reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        _detail = reason;
        Status = StartupStepStatus.Skipped;
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
    }
}
