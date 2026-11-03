using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Dialogs;

/// <summary>Shows one modal dialog at a time over the main window; the window draws <see cref="Current"/>.</summary>
public interface IDialogService
{
    DialogViewModel? Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Creates a dialog with its services injected and extra arguments passed in, and shows it (closing the current one).</summary>
    T Show<T>(params object[] args) where T : DialogViewModel;

    /// <summary>Closes <paramref name="dialog"/> if it is the one shown. No-op otherwise.</summary>
    void Close(DialogViewModel dialog);
}

internal sealed class DialogService(IServiceProvider services) : IDialogService
{
    public DialogViewModel? Current { get; private set; }

    public event EventHandler? Changed;

    public T Show<T>(params object[] args) where T : DialogViewModel
    {
        var dialog = ActivatorUtilities.CreateInstance<T>(services, args);
        var previous = Current;
        Current = dialog;
        previous?.OnClosed();
        Changed?.Invoke(this, EventArgs.Empty);
        return dialog;
    }

    public void Close(DialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (Current != dialog)
        {
            return;
        }

        Current = null;
        dialog.OnClosed();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
