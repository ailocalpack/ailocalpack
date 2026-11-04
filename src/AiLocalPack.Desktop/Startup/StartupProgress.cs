using System.ComponentModel;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.Startup;

/// <summary>
/// The startup checklist (design §9) as the splash and the chrome show it: blocking steps (before the library opens)
/// and background checks (while it is in use), with their totals. Follows its steps; UI thread only.
/// </summary>
public sealed class StartupProgress : ObservableObject
{
    private readonly Localizer _localizer;

    public StartupProgress(IEnumerable<StartupStep> steps, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(localizer);
        _localizer = localizer;
        Steps = [.. steps];
        Blocking = [.. Steps.Where(s => s.Phase == StartupPhase.Blocking)];
        Background = [.. Steps.Where(s => s.Phase == StartupPhase.Background)];
        foreach (var step in Steps)
        {
            step.PropertyChanged += OnStepChanged;
        }

        _localizer.CultureChanged += (_, _) => OnPropertyChanged(nameof(BackgroundSummary));
    }

    public IReadOnlyList<StartupStep> Steps { get; }

    public IReadOnlyList<StartupStep> Blocking { get; }

    public IReadOnlyList<StartupStep> Background { get; }

    public StartupStep? Find(string id) => Steps.FirstOrDefault(s => s.Id == id);

    /// <summary>The blocking step running now, else the last one that finished (the splash always names a step).</summary>
    public StartupStep? Current => Blocking.FirstOrDefault(s => s.IsRunning) ?? Blocking.LastOrDefault(s => s.IsFinished) ?? Blocking.FirstOrDefault();

    /// <summary>Share of the blocking phase done, 0–100 (a running step counts half).</summary>
    public double BlockingPercent => Blocking.Count == 0
        ? 100
        : 100.0 * Blocking.Sum(s => s.IsFinished ? 1 : s.IsRunning ? 0.5 : 0) / Blocking.Count;

    public bool IsBlockingDone => Blocking.All(s => s.IsFinished);

    /// <summary>A background check is running: the chrome says "Checking for changes…" and spins the ring on 🔔.</summary>
    public bool IsBackgroundRunning => Background.Any(s => s.IsRunning);

    public int BackgroundFinished => Background.Count(s => s.IsFinished);

    /// <summary>"2 of 4" in the startup checks list.</summary>
    public string BackgroundSummary => _localizer.Format(nameof(Strings.Startup_Summary), BackgroundFinished, Background.Count);

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StartupStep.Status))
        {
            return;
        }

        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(BlockingPercent));
        OnPropertyChanged(nameof(IsBlockingDone));
        OnPropertyChanged(nameof(IsBackgroundRunning));
        OnPropertyChanged(nameof(BackgroundFinished));
        OnPropertyChanged(nameof(BackgroundSummary));
    }
}
