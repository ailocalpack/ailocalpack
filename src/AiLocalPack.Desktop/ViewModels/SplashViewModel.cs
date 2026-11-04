using System.ComponentModel;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Startup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Splash window (design §9): logo, version, the blocking step running now and one progress bar. <c>Details</c> lists
/// the blocking steps; it opens by itself when a step runs long ("Taking longer than usual") and stays as the player
/// left it once toggled. A step that ended with a warning shows it in a note.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject
{
    /// <summary>A blocking step running this long counts as slow.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(3);

    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private bool? _detailsChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsOpen), nameof(ToggleText), nameof(SlowText))]
    private bool _isSlow;

    public SplashViewModel(StartupProgress progress, Localizer localizer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(time);
        Progress = progress;
        _localizer = localizer;
        _time = time;
        Progress.PropertyChanged += OnProgressChanged;
        foreach (var step in Progress.Blocking)
        {
            step.PropertyChanged += (_, _) => OnTextsChanged();
        }

        _localizer.CultureChanged += (_, _) =>
        {
            OnTextsChanged();
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(ToggleText));
        };
    }

    public StartupProgress Progress { get; }

    public IReadOnlyList<StartupStep> Steps => Progress.Blocking;

    public string Version => _localizer.Format(nameof(Strings.Splash_Version), AppVersion.Current);

    /// <summary>"Opening your game library…" (with its progress line while it has one).</summary>
    public string CurrentText
    {
        get
        {
            if (Progress.Current is not { } step)
            {
                return string.Empty;
            }

            var text = _localizer.Format(nameof(Strings.Splash_Running), step.Label);
            return step.IsRunning && step.HasDetail ? $"{text} · {step.Detail}" : text;
        }
    }

    public double Percent => Progress.BlockingPercent;

    /// <summary>The result of a blocking step that ended with a warning (damaged game list), shown as a note.</summary>
    public string? Warning => Progress.Blocking.FirstOrDefault(s => s.IsWarn)?.Detail;

    public bool HasWarning => Warning is not null;

    public string SlowText => _localizer.Format(
        nameof(Strings.Splash_Slow), Progress.Blocking.Count(s => s.IsFinished), Progress.Blocking.Count);

    /// <summary>The steps list is shown: as the player toggled it, else open while a step is slow.</summary>
    public bool IsDetailsOpen => _detailsChoice ?? IsSlow;

    public string ToggleText => _localizer[IsDetailsOpen ? nameof(Strings.Splash_HideDetails) : nameof(Strings.Splash_Details)];

    [RelayCommand]
    private void ToggleDetails()
    {
        _detailsChoice = !IsDetailsOpen;
        OnPropertyChanged(nameof(IsDetailsOpen));
        OnPropertyChanged(nameof(ToggleText));
    }

    /// <summary>Re-checks whether the running step is slow; the splash window calls it on a timer.</summary>
    public void CheckSlow() =>
        IsSlow = Progress.Current is { IsRunning: true, StartedAt: { } started } && _time.GetUtcNow() - started >= SlowAfter;

    private void OnProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StartupProgress.Current) or nameof(StartupProgress.BlockingPercent))
        {
            OnPropertyChanged(nameof(Percent));
            CheckSlow();
            OnTextsChanged();
        }
    }

    private void OnTextsChanged()
    {
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(SlowText));
    }
}
