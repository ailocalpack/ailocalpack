using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.Dialogs;

/// <summary>A modal dialog shown over the main window (scrim, centered card) by <see cref="IDialogService"/>.</summary>
public abstract class DialogViewModel : ObservableObject
{
    /// <summary>Lets the dialog use Esc first (leave a text field, stop a step); returns <c>false</c> to close the dialog.</summary>
    public virtual bool TryHandleEscape() => false;

    /// <summary>Called once when the dialog leaves the screen (closed by itself, Esc or another dialog): stop background work here.</summary>
    public virtual void OnClosed()
    {
    }
}
