using AiLocalPack.Desktop.ViewModels.GamePage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AiLocalPack.Desktop.Views;

public sealed partial class GenerationTabView : UserControl
{
    public GenerationTabView() => InitializeComponent();

    /// <summary>A tap anywhere on an agent card chooses it, not only on its radio button (its own buttons keep their action).</summary>
    private void OnAgentCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: GenerationAgentCard card } && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            card.SelectCommand.Execute(null);
        }
    }

    /// <summary>Picking a language closes the popover (the command switches the form's language).</summary>
    private void OnLanguagePicked(object? sender, RoutedEventArgs e) => GenLanguageButton.Flyout?.Hide();

    /// <summary>
    /// Runs the row's (or Compare's) command before the popover closes: hiding it first detaches the row, its command
    /// binding goes with it, and the click would only close the popover.
    /// </summary>
    private void OnQualityPicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Command: { } command } button && command.CanExecute(button.CommandParameter))
        {
            command.Execute(button.CommandParameter);
        }

        e.Handled = true;
        GenQualityButton.Flyout?.Hide();
    }
}
