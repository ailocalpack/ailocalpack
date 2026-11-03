using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace AiLocalPack.Desktop.Services;

/// <summary>The OS folder and file pickers; behind an interface so view models are tested without dialogs.</summary>
public interface IFilePicker
{
    /// <summary>Full path of the folder the player picked, or <c>null</c> when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Full path of the image (PNG, JPEG, WebP, BMP) the player picked, or <c>null</c> when cancelled.</summary>
    Task<string?> PickImageAsync(string title);
}

internal sealed class AvaloniaFilePicker : IFilePicker
{
    private static readonly FilePickerFileType Images = new("Images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
        MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"],
        AppleUniformTypeIdentifiers = ["public.png", "public.jpeg", "org.webmproject.webp", "com.microsoft.bmp"],
    };

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Storage() is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickImageAsync(string title)
    {
        if (Storage() is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [Images] });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static IStorageProvider? Storage() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window window }
            ? window.StorageProvider
            : null;
}
