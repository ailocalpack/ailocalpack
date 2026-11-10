using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.GitHub;

/// <summary>
/// The player's GitHub sign-in as the app's screens use it (task 59): the remembered account, Device Flow, the check
/// with GitHub, sign-out and the account's avatar. Behind an interface so view models are tested with a fake (no
/// network, no keychain); the real one is <see cref="GitHubHost"/> over <see cref="GitHubSession"/>. Every method may be
/// called off the UI thread and never throws but for cancellation.
/// </summary>
public interface IGitHubHost
{
    /// <summary>Raised after a sign-in, sign-out, or a check that changed the account; any thread.</summary>
    event EventHandler? Changed;

    GitHubOAuthApp App { get; }

    /// <summary>Where the token is kept (the keychain note).</summary>
    SecretStoreKind KeychainKind { get; }

    /// <summary>The remembered account; no network, no keychain.</summary>
    GitHubAccount? Account { get; }

    Task<DeviceCodeResult> StartSignInAsync(CancellationToken cancellationToken = default);

    Task<GitHubSignInResult> FinishSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default);

    Task<GitHubAccountCheck> CheckAsync(CancellationToken cancellationToken = default);

    Task<SecretResult> SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>The account's avatar, small; <c>null</c> when it can't be loaded (offline, no avatar): the view draws a letter.</summary>
    Task<Bitmap?> LoadAvatarAsync(Uri avatar, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IGitHubHost"/> over the app's <see cref="GitHubSession"/>; avatars come from GitHub's CDN, kept in memory.</summary>
internal sealed class GitHubHost(GitHubSession session, HttpClient http, ILogger<GitHubHost> logger) : IGitHubHost
{
    /// <summary>Decoded width: twice the 40 px avatar for HiDPI screens.</summary>
    private const int AvatarSize = 80;

    /// <summary>An avatar is a few KB; anything far bigger isn't one.</summary>
    private const int MaxAvatarBytes = 2 * 1024 * 1024;

    private readonly Dictionary<Uri, Bitmap> _avatars = [];
    private readonly Lock _lock = new();

    public event EventHandler? Changed
    {
        add => session.Changed += value;
        remove => session.Changed -= value;
    }

    public GitHubOAuthApp App => session.App;

    public SecretStoreKind KeychainKind => session.KeychainKind;

    public GitHubAccount? Account => session.Account;

    public Task<DeviceCodeResult> StartSignInAsync(CancellationToken cancellationToken = default) => session.StartSignInAsync(cancellationToken);

    public Task<GitHubSignInResult> FinishSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default) =>
        session.FinishSignInAsync(authorization, cancellationToken);

    public Task<GitHubAccountCheck> CheckAsync(CancellationToken cancellationToken = default) => session.CheckAsync(cancellationToken);

    public Task<SecretResult> SignOutAsync(CancellationToken cancellationToken = default) => session.SignOutAsync(cancellationToken);

    public async Task<Bitmap?> LoadAvatarAsync(Uri avatar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        lock (_lock)
        {
            if (_avatars.TryGetValue(avatar, out var known))
            {
                return known;
            }
        }

        if (avatar.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            // GitHub's avatar CDN scales on request (s = size in px).
            var sized = new UriBuilder(avatar) { Query = (avatar.Query.Length > 1 ? avatar.Query[1..] + "&" : string.Empty) + $"s={AvatarSize}" }.Uri;
            using var response = await http.GetAsync(sized, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxAvatarBytes)
            {
                logger.LogInformation("GitHub avatar {Url}: HTTP {Status}", avatar, (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxAvatarBytes)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = Bitmap.DecodeToWidth(stream, AvatarSize, BitmapInterpolationMode.HighQuality);
            lock (_lock)
            {
                _avatars[avatar] = bitmap;
            }

            return bitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Offline, a timeout, a broken image (the decoder's exception types vary): the letter instead.
            logger.LogInformation(e, "Can't load the GitHub avatar {Url}", avatar);
            return null;
        }
    }
}
