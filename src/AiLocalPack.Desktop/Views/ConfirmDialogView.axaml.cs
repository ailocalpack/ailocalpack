using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class ConfirmDialogView : UserControl
{
    public ConfirmDialogView() => InitializeComponent();

    // The safe choice has the focus, so Enter never confirms a destructive action by accident.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => CancelButton.Focus(NavigationMethod.Tab));
    }
}
