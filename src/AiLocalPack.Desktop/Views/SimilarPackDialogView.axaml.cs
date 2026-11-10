using AiLocalPack.Desktop.Publishing;
using Avalonia.Controls;
using Avalonia.Input;

namespace AiLocalPack.Desktop.Views;

public sealed partial class SimilarPackDialogView : UserControl
{
    public SimilarPackDialogView() => InitializeComponent();

    /// <summary>A click anywhere on a card picks it, not only on its radio button.</summary>
    private void OnRevisionTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SimilarPackDialogViewModel dialog)
        {
            dialog.ChooseRevisionCommand.Execute(null);
        }
    }

    private void OnSeparateTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SimilarPackDialogViewModel dialog)
        {
            dialog.ChooseSeparateCommand.Execute(null);
        }
    }
}
