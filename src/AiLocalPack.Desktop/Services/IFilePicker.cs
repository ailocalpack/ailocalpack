using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Services;

/// <summary>
/// An image on the clipboard: an image file the player copied (<see cref="Path"/>, e.g. in Finder / Explorer) or
/// image data (<see cref="Png"/>, a screenshot taken to the clipboard, an image copied in a browser), as PNG.
/// </summary>
public sealed record ClipboardImage(string? Path, byte[]? Png);

/// <summary>The OS folder and file pickers and the clipboard's images; behind an interface so view models are tested without dialogs.</summary>
public interface IFilePicker
{
    /// <summary>Full path of the folder the player picked, or <c>null</c> when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Full path of the image (PNG, JPEG, WebP, BMP) the player picked, or <c>null</c> when cancelled.</summary>
    Task<string?> PickImageAsync(string title);

    /// <summary>
    /// The image on the clipboard, or <c>null</c> when it holds none (or text, which a paste keeps as text). Never throws.
    /// </summary>
    Task<ClipboardImage?> ReadClipboardImageAsync();
}

internal sealed class AvaloniaFilePicker(ILogger<AvaloniaFilePicker> logger) : IFilePicker
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];


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

    public async Task<ClipboardImage?> ReadClipboardImageAsync()
    {
        if (MainWindow()?.Clipboard is not { } clipboard)
        {
            return null;
        }

        try
        {
            // A copied file comes first: Finder also puts the file's icon on the clipboard as an image.
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                var path = files.Select(f => f.TryGetLocalPath())
                    .FirstOrDefault(p => p is not null && ImageExtensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
                return path is null ? null : new ClipboardImage(path, null);
            }

            // Text wins over a picture of it (Excel, Word and some editors put both).
            var formats = await clipboard.GetDataFormatsAsync();
            if (formats.Contains(DataFormat.Text) || !formats.Contains(DataFormat.Bitmap))
            {
                return null;
            }

            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                return null;
            }

            using var png = new MemoryStream();
            bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return new ClipboardImage(null, png.ToArray());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Platform clipboards fail in many ways (COM, D-Bus, a format that doesn't decode); a paste then stays text.
            logger.LogWarning(e, "Couldn't read an image from the clipboard");
            return null;
        }
    }

    private static Window? MainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window window } ? window : null;

    private static IStorageProvider? Storage() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window window }
            ? window.StorageProvider
            : null;
}
