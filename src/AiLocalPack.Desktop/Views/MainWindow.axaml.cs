using AiLocalPack.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace AiLocalPack.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
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
