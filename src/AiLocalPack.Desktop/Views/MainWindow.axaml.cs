using AiLocalPack.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
        ActivityFlyout = (Flyout)ActivityButton.Flyout!;
        ActivityFlyout.Opened += (_, _) =>
        {
            (DataContext as MainWindowViewModel)?.Activity?.OnOpened();
            Dispatcher.UIThread.Post(ActivityPanel.FocusFirstRow, DispatcherPriority.Loaded);
        };
        // Closed (outside click, Esc, a row opened a page): the focus goes back to 🔔.
        ActivityFlyout.Closed += (_, _) => ActivityButton.Focus();
        ActivityPanel.EscapePressed += (_, _) => ActivityFlyout.Hide();
    }

    /// <summary>The Activity panel's popover under 🔔.</summary>
    private Flyout ActivityFlyout { get; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel { Activity: { } activity })
        {
            activity.CloseRequested += (_, _) => ActivityFlyout.Hide();
        }
    }

    // Mouse "back" button (XButton1) goes back, like in a browser.
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && DataContext is MainWindowViewModel vm)
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
    }
}
