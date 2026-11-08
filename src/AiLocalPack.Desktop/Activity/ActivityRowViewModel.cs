using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Covers;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Activity;

/// <summary>Where a row sits in the panel.</summary>
public enum ActivityRowKind
{
    /// <summary><i>Needs your decision</i>: a card with a tone stripe, 🕑 <i>Decide later</i>.</summary>
    Decision,

    /// <summary><i>Running</i>: bar, step, percent.</summary>
    Running,

    /// <summary>The feed: unread dot, ✕ to hide.</summary>
    Event,
}

/// <summary>A button on a row (<c>Retry</c>, <c>Log</c>, <c>Update to rev 3</c>…).</summary>
public sealed record ActivityActionViewModel(string Text, string? Icon, bool IsPrimary, IRelayCommand Command)
{
    public bool HasIcon => Icon is not null;
}

/// <summary>One row of the Activity panel (design §5).</summary>
public sealed partial class ActivityRowViewModel : ObservableObject
{
    private IBrush? _placeholder;

    public ActivityRowViewModel(
        string id,
        ActivityRowKind kind,
        GameId? game,
        string gameName,
        ActivityTone tone,
        string toneText,
        Action open)
    {
        Id = id;
        Kind = kind;
        Game = game;
        GameName = gameName;
        Tone = tone;
        ToneText = toneText;
        OpenCommand = new RelayCommand(open);
    }

    /// <summary>The event's or task's ID.</summary>
    public string Id { get; }

    public ActivityRowKind Kind { get; }

    public GameId? Game { get; }

    public string GameName { get; }

    /// <summary>The cover's first letter on the placeholder.</summary>
    public string Initial => GameName.Length > 0 ? GameName[..1].ToUpperInvariant() : "a";

    public ActivityTone Tone { get; }

    public string ToneText { get; }

    public string ToneIcon => ActivityTexts.ToneIcon(Tone);

    public bool IsDecision => Kind == ActivityRowKind.Decision;

    public bool IsRunning => Kind == ActivityRowKind.Running;

    public bool IsEvent => Kind == ActivityRowKind.Event;

    public bool IsAttention => Tone == ActivityTone.Attention;

    public bool IsBad => Tone == ActivityTone.Bad;

    public bool IsOk => Tone == ActivityTone.Ok;

    /// <summary>A feed event the player hasn't seen: dot + bold title.</summary>
    [ObservableProperty]
    public partial bool IsUnread { get; set; }

    /// <summary>A read feed event: title in the muted color.</summary>
    public bool IsRead => IsEvent && !IsUnread;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; set; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>"14:05" (feed, decisions) or "60 %" (running).</summary>
    [ObservableProperty]
    public partial string? Time { get; set; }

    /// <summary>Running: the bar, 0–100.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary>Running: "Backing up the original files · 60 %".</summary>
    [ObservableProperty]
    public partial string? Step { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    public partial IReadOnlyList<ActivityActionViewModel> Actions { get; set; } = [];

    public bool HasActions => Actions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial Bitmap? Cover { get; set; }

    public bool HasCover => Cover is not null;

    public IBrush PlaceholderBrush => _placeholder ??= Game is { } game ? PlaceholderColors.CreateBrush(game) : Brushes.Gray;

    /// <summary>Opens the row: the game's page (and marks a feed event read).</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>✕ on a feed event; <c>null</c> elsewhere.</summary>
    public IRelayCommand? HideCommand { get; init; }

    /// <summary>🕑 on a decision; <c>null</c> elsewhere.</summary>
    public IRelayCommand? DecideLaterCommand { get; init; }

    /// <summary>⏸ on a pausable task (generation, task 52); <c>null</c> elsewhere.</summary>
    public IRelayCommand? PauseCommand { get; init; }

    /// <summary>✕ on a cancellable task (a queued one); <c>null</c> elsewhere.</summary>
    public IRelayCommand? CancelCommand { get; init; }

    public bool CanHide => HideCommand is not null;

    public bool CanDecideLater => DecideLaterCommand is not null;

    public bool CanPause => PauseCommand is not null;

    public bool CanCancel => CancelCommand is not null;

    partial void OnIsUnreadChanged(bool value) => OnPropertyChanged(nameof(IsRead));
}

/// <summary>A day of the feed: its heading and rows.</summary>
public sealed record ActivityDayViewModel(string Heading, string? NewText, IReadOnlyList<ActivityRowViewModel> Rows)
{
    public bool HasNew => NewText is not null;
}
