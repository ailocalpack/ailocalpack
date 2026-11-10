using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Dialogs;

/// <summary>What a confirmation asks: the question, what is <em>not</em> affected, the confirming button's text.</summary>
/// <param name="Title">The question ("Remove «Wren &amp; Whistle» from AI Local Pack?").</param>
/// <param name="Message">What happens and what stays (design §8: destructive actions say what is not affected).</param>
/// <param name="ConfirmText">Text of the confirming button ("Remove").</param>
/// <param name="OnConfirm">Runs after the dialog closed, when the player confirmed.</param>
/// <param name="IsDestructive">The confirming button is red.</param>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText, Action OnConfirm, bool IsDestructive = true);

/// <summary>A yes / no question before a destructive action. Esc and Cancel close it without doing anything.</summary>
public sealed partial class ConfirmDialogViewModel(ConfirmRequest request, IDialogService dialogs) : DialogViewModel
{
    public ConfirmRequest Request { get; } = request ?? throw new ArgumentNullException(nameof(request));

    public string Title => Request.Title;

    public string Message => Request.Message;

    public string ConfirmText => Request.ConfirmText;

    public bool IsDestructive => Request.IsDestructive;

    [RelayCommand]
    private void Confirm()
    {
        dialogs.Close(this);
        Request.OnConfirm();
    }

    [RelayCommand]
    private void Cancel() => dialogs.Close(this);
}
