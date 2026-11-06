using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class InstallConfirmDialogView : UserControl
{
    public InstallConfirmDialogView() => InitializeComponent();

    // A risky install is confirmed on purpose: Cancel has the focus, so Enter doesn't install.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => CancelButton.Focus(NavigationMethod.Tab));
    }
}
