using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class GenerationCancelDialogView : UserControl
{
    public GenerationCancelDialogView() => InitializeComponent();

    // Keeping the progress is the safe choice, so it has the focus: Enter never deletes the working copy by accident.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => KeepButton.Focus(NavigationMethod.Tab));
    }
}
