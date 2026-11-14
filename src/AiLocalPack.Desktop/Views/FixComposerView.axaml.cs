using AiLocalPack.Desktop.ViewModels.GamePage;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiLocalPack.Desktop.Views;

public sealed partial class FixComposerView : UserControl
{
    private bool _pastingText;

    public FixComposerView()
    {
        InitializeComponent();
        FixRequest.AddHandler(TextBox.PastingFromClipboardEvent, OnPasting);
    }

    /// <summary>
    /// A paste into the request: an image on the clipboard is attached as a screenshot (<see cref="FixViewModel.PasteImageAsync"/>);
    /// anything else is pasted as text, by pasting again past this handler (the event comes before the text box reads the clipboard).
    /// </summary>
    private async void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (_pastingText || DataContext is not FixViewModel fix)
        {
            return;
        }

        e.Handled = true;
        if (await fix.PasteImageAsync())
        {
            return;
        }

        _pastingText = true;
        try
        {
            FixRequest.Paste();
        }
        finally
        {
            _pastingText = false;
        }
    }
}
