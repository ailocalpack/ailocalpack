using AiLocalPack.Desktop.Agents;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiLocalPack.Desktop.Views;

public sealed partial class LlmDialogView : UserControl
{
    public LlmDialogView() => InitializeComponent();

    /// <summary>The address is checked when it loses focus, not on every key (and the model list follows it).</summary>
    /// <summary>A model picked from the server's list goes into the field and the list closes.</summary>
    private void OnModelPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string model } list && DataContext is LlmDialogViewModel dialog)
        {
            dialog.Model = model;
            list.SelectedItem = null;
            LlmModelList.Flyout?.Hide();
        }
    }

    private void OnUrlLostFocus(object? sender, RoutedEventArgs e) => (DataContext as LlmDialogViewModel)?.OnUrlLeft();
}
