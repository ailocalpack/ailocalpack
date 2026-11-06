using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Services;

/// <summary>A toast's color: green done, red failure, neutral info.</summary>
public enum ToastTone
{
    Info,
    Ok,
    Bad,
}

/// <summary>A button on a toast; pressing it closes the toast, then runs.</summary>
/// <param name="Icon">Resource key of the icon, or <c>null</c>.</param>
public sealed record ToastAction(string Text, Action Run, string? Icon = null, bool IsPrimary = false);

/// <summary>
/// A message in the window's bottom right corner (design §4a: "Installed: «…». ▶ Play · Undo"; "Couldn't install; files
/// restored. Retry · Details"). Stays until closed or replaced by a toast with the same <see cref="Key"/>.
/// </summary>
public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(ToastTone tone, string text, IReadOnlyList<ToastAction> actions, string? key = null)
    {
        Tone = tone;
        Text = text;
        Key = key;
        Actions = [.. actions.Select(a => new ToastButton(a, this))];
    }

    public ToastTone Tone { get; }

    public string Text { get; }

    /// <summary>A toast with the same key replaces this one (e.g. one per game).</summary>
    public string? Key { get; }

    public IReadOnlyList<ToastButton> Actions { get; }

    public bool IsOk => Tone == ToastTone.Ok;

    public bool IsBad => Tone == ToastTone.Bad;

    public string Icon => Tone switch
    {
        ToastTone.Ok => "IconCheckCircle",
        ToastTone.Bad => "IconXCircle",
        _ => "IconInfo",
    };

    /// <summary>Set by <see cref="ToastService"/>.</summary>
    internal ToastService? Owner { get; set; }

    [RelayCommand]
    private void Close() => Owner?.Close(this);
}

/// <summary>A toast button bound in the view.</summary>
public sealed class ToastButton(ToastAction action, ToastViewModel toast)
{
    public string Text => action.Text;

    public string? Icon => action.Icon;

    public bool HasIcon => action.Icon is not null;

    public bool IsPrimary => action.IsPrimary;

    public IRelayCommand Command { get; } = new RelayCommand(() =>
    {
        toast.CloseCommand.Execute(null);
        action.Run();
    });
}

/// <summary>The toasts shown over the window, newest last; at most <see cref="MaxShown"/>. Use on the UI thread.</summary>
public sealed class ToastService
{
    public const int MaxShown = 3;

    public ObservableCollection<ToastViewModel> Items { get; } = [];

    public void Show(ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (toast.Key is { } key && Items.FirstOrDefault(t => t.Key == key) is { } same)
        {
            Close(same);
        }

        toast.Owner = this;
        Items.Add(toast);
        while (Items.Count > MaxShown)
        {
            Close(Items[0]);
        }
    }

    public void Close(ToastViewModel toast)
    {
        if (Items.Remove(toast))
        {
            toast.Owner = null;
        }
    }
}
