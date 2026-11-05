using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiLocalPack.Desktop.Views;

public sealed partial class PacksTabView : UserControl
{
    public PacksTabView() => InitializeComponent();

    /// <summary>Picking a language closes the popover (the command switches the list).</summary>
    private void OnLanguagePicked(object? sender, RoutedEventArgs e) => LanguageButton.Flyout?.Hide();
}
