using AiLocalPack.Core.Games;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Covers;

/// <summary>Loads cover images for the library grid; <c>null</c> = draw the placeholder.</summary>
public interface ICoverLoader
{
    Task<Bitmap?> LoadAsync(Game game, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves covers (<see cref="CoverResolver"/>) and decodes them off the UI thread, scaled down to the card size, a
/// few at a time so a big library doesn't flood the disk and the CDN at startup.
/// </summary>
internal sealed class CoverLoader(CoverResolver resolver, ILogger<CoverLoader> logger) : ICoverLoader, IDisposable
{
    /// <summary>Decoded width: twice the widest card (≈ 220 px) for sharp covers on HiDPI screens.</summary>
    public const int DecodeWidth = 440;

    private readonly SemaphoreSlim _slots = new(4);

    public async Task<Bitmap?> LoadAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await resolver.ResolveAsync(game, cancellationToken).ConfigureAwait(false) is not { } path)
            {
                return null;
            }

            return await Task.Run(() => Decode(path), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();

    private Bitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e)
        {
            // Any failure (unreadable file, broken or odd image; the decoder's exception types vary) means the placeholder.
            logger.LogWarning(e, "Can't decode cover {Path}", path);
            return null;
        }
    }
}

/// <summary>No covers (tests, previews): every card shows its placeholder.</summary>
public sealed class NoCoverLoader : ICoverLoader
{
    public static NoCoverLoader Instance { get; } = new();

    public Task<Bitmap?> LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.FromResult<Bitmap?>(null);
}
