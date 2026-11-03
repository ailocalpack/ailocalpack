using AiLocalPack.Core.Games;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Covers;

/// <summary>A decoded cover. An icon (a manual game's executable icon) is drawn small on the placeholder, not cropped to 2:3.</summary>
public sealed record CoverImage(Bitmap Bitmap, bool IsIcon);

/// <summary>Loads cover images; <c>null</c> = draw the placeholder.</summary>
public interface ICoverLoader
{
    /// <summary>The game's cover: a manual game's stored cover (<see cref="ManualCoverStore"/>), a Steam game's art.</summary>
    Task<CoverImage?> LoadAsync(Game game, CancellationToken cancellationToken = default);

    /// <summary>Decodes an image in memory (a game icon, a picked image in the add-folder dialog); <c>null</c> if it can't be decoded.</summary>
    Task<CoverImage?> DecodeAsync(byte[] image, bool isIcon, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves covers (<see cref="ManualCoverStore"/> for manual games, <see cref="CoverResolver"/> for Steam) and decodes
/// them off the UI thread, scaled down to the card size, a few at a time so a big library doesn't flood the disk and the
/// CDN at startup.
/// </summary>
internal sealed class CoverLoader(CoverResolver resolver, ManualCoverStore manual, ILogger<CoverLoader> logger) : ICoverLoader, IDisposable
{
    /// <summary>Decoded width: twice the widest card (≈ 220 px) for sharp covers on HiDPI screens.</summary>
    public const int DecodeWidth = 440;

    /// <summary>Icons are drawn at about half the card width.</summary>
    public const int IconDecodeWidth = 256;

    private readonly SemaphoreSlim _slots = new(4);

    public async Task<CoverImage?> LoadAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (manual.Find(game.Id) is { } stored)
            {
                return await Task.Run(() => DecodeFile(stored.Path, stored.IsIcon), cancellationToken).ConfigureAwait(false);
            }

            if (await resolver.ResolveAsync(game, cancellationToken).ConfigureAwait(false) is not { } path)
            {
                return null;
            }

            return await Task.Run(() => DecodeFile(path, isIcon: false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    public Task<CoverImage?> DecodeAsync(byte[] image, bool isIcon, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Task.Run(() => Decode(() => new MemoryStream(image, writable: false), isIcon, "(memory)"), cancellationToken);
    }

    public void Dispose() => _slots.Dispose();

    private CoverImage? DecodeFile(string path, bool isIcon) => Decode(() => File.OpenRead(path), isIcon, path);

    private CoverImage? Decode(Func<Stream> open, bool isIcon, string source)
    {
        try
        {
            using var stream = open();
            var bitmap = Bitmap.DecodeToWidth(stream, isIcon ? IconDecodeWidth : DecodeWidth, BitmapInterpolationMode.HighQuality);
            return new CoverImage(bitmap, isIcon);
        }
        catch (Exception e)
        {
            // Any failure (unreadable file, broken or odd image; the decoder's exception types vary) means the placeholder.
            logger.LogWarning(e, "Can't decode cover {Path}", source);
            return null;
        }
    }
}

/// <summary>No covers (tests, previews): every card shows its placeholder.</summary>
public sealed class NoCoverLoader : ICoverLoader
{
    public static NoCoverLoader Instance { get; } = new();

    public Task<CoverImage?> LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.FromResult<CoverImage?>(null);

    public Task<CoverImage?> DecodeAsync(byte[] image, bool isIcon, CancellationToken cancellationToken = default) =>
        Task.FromResult<CoverImage?>(null);
}
