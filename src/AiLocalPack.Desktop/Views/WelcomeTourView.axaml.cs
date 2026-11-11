using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class WelcomeTourView : UserControl
{
    public WelcomeTourView() => InitializeComponent();

    // The tour starts with the focus on its main button: Enter goes on, Esc closes.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => NextButton.Focus(NavigationMethod.Tab));
    }
}
