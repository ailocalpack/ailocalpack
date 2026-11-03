using AiLocalPack.Desktop.ViewModels.AddGame;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class AddGameDialogView : UserControl
{
    public AddGameDialogView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZoneFrame.Classes.Set("active", false));
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        NameBox.KeyDown += OnNameKeyDown;
        NameBox.PropertyChanged += (_, e) =>
        {
            // ✎ turns the name into a field: put the caret there.
            if (e.Property == IsVisibleProperty && NameBox.IsVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    NameBox.Focus(NavigationMethod.Tab);
                    NameBox.SelectAll();
                });
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => ChooseFolderButton.Focus(NavigationMethod.Tab));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropZoneFrame.Classes.Set("active", hasFiles);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZoneFrame.Classes.Set("active", false);
        if (DataContext is AddGameDialogViewModel vm && DroppedFolder.From(e) is { } folder)
        {
            e.Handled = true;
            _ = vm.AnalyzeAsync(folder);
        }
    }

    // Enter or Esc in the name field ends renaming (Esc is also handled by the window, through the view model).
    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is AddGameDialogViewModel vm)
        {
            vm.IsRenaming = false;
            e.Handled = true;
        }
    }
}

/// <summary>The folder of a drag-and-drop: the first dropped item; a dropped file stands for its folder.</summary>
internal static class DroppedFolder
{
    public static string? From(DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is null)
        {
            return null;
        }

        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }
}
