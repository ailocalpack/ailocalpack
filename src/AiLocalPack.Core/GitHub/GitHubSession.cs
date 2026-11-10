using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.GitHub;

/// <summary>How <see cref="GitHubSession.FinishSignInAsync"/> ended.</summary>
public abstract record GitHubSignInResult
{
    private GitHubSignInResult()
    {
    }

    public sealed record SignedIn(GitHubAccount Account) : GitHubSignInResult;

    /// <summary>The code ran out before the player approved it.</summary>
    public sealed record Expired : GitHubSignInResult;

    /// <summary>The player pressed Cancel on GitHub's page.</summary>
    public sealed record Denied : GitHubSignInResult;

    public sealed record Failed(GitHubAuthProblem Problem, string Detail) : GitHubSignInResult;

    /// <summary>
    /// GitHub approved, but the keychain couldn't keep the token (Linux without a keyring, a declined unlock prompt):
    /// the app stays signed out rather than keep the token in plain text.
    /// </summary>
    public sealed record NotSaved(GitHubUser User, SecretResult Keychain) : GitHubSignInResult;
}

/// <summary>What <see cref="GitHubSession.CheckAsync"/> found.</summary>
public abstract record GitHubAccountCheck
{
    private GitHubAccountCheck()
    {
    }

    /// <summary>GitHub confirmed the token just now; the account is refreshed (new name, avatar).</summary>
    public sealed record SignedIn(GitHubAccount Account) : GitHubAccountCheck;

    /// <summary>A token is kept but GitHub couldn't be asked: the remembered account, if any.</summary>
    public sealed record Unverified(GitHubAccount? Account, string Detail) : GitHubAccountCheck;

    public sealed record SignedOut : GitHubAccountCheck;

    /// <summary>GitHub rejected the token (revoked on github.com): the app signed out.</summary>
    public sealed record Revoked(string Login) : GitHubAccountCheck;

    /// <summary>The keychain couldn't be read: the remembered account, if any.</summary>
    public sealed record KeychainProblem(GitHubAccount? Account, SecretResult Keychain) : GitHubAccountCheck;
}

/// <summary>
/// The player's GitHub sign-in (task 58): Device Flow, the token in the OS keychain (secret <c>github-token</c>), the
/// account in <c>&lt;data&gt;/github-account.json</c> for showing it offline. <see cref="Account"/> reads no network;
/// <see cref="CheckAsync"/> asks GitHub. Sign-out deletes the token and the account here (the app has no client secret,
/// so it can't revoke the token on GitHub; <see cref="GitHubOAuthApp.ConnectionsPage"/> does). Never throws except for
/// cancellation.
/// </summary>
public sealed class GitHubSession
{
    /// <summary>The keychain entry holding the OAuth token.</summary>
    public const string TokenSecret = "github-token";

    private readonly ISecretStore _secrets;
    private readonly GitHubDeviceFlow _flow;
    private readonly GitHubUserClient _users;
    private readonly GitHubAccountStore _accounts;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubSession(
        ISecretStore secrets,
        GitHubDeviceFlow flow,
        GitHubUserClient users,
        AppPaths paths,
        GitHubOAuthApp app,
        TimeProvider? time = null,
        ILogger<GitHubSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(app);
        _secrets = secrets;
        _flow = flow;
        _users = users;
        App = app;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _accounts = new GitHubAccountStore(paths, _time, _logger);
    }

    /// <summary>Raised after a sign-in, sign-out, or a check that changed the account.</summary>
    public event EventHandler? Changed;

    public GitHubOAuthApp App { get; }

    /// <summary>Where the token is kept (the keychain note in Settings).</summary>
    public SecretStoreKind KeychainKind => _secrets.Kind;

    /// <summary>The remembered account, without network or keychain (the keychain may ask the player).</summary>
    public GitHubAccount? Account => _accounts.Load();

    /// <summary>Asks GitHub for a user code to show.</summary>
    public Task<DeviceCodeResult> StartSignInAsync(CancellationToken cancellationToken = default) => _flow.StartAsync(cancellationToken);

    /// <summary>Waits for the player to approve the code, then keeps the token in the keychain and remembers the account.</summary>
    public async Task<GitHubSignInResult> FinishSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var granted = await _flow.WaitForTokenAsync(authorization, cancellationToken).ConfigureAwait(false);
        switch (granted)
        {
            case DeviceTokenResult.Expired:
                return new GitHubSignInResult.Expired();
            case DeviceTokenResult.Denied:
                return new GitHubSignInResult.Denied();
            case DeviceTokenResult.Failed failed:
                return new GitHubSignInResult.Failed(failed.Problem, failed.Detail);
        }

        var token = ((DeviceTokenResult.Granted)granted).Token;
        var scopes = ((DeviceTokenResult.Granted)granted).Scopes;
        GitHubUser user;
        switch (await _users.GetUserAsync(token, cancellationToken).ConfigureAwait(false))
        {
            case GitHubUserResult.Found found:
                user = found.User;
                scopes = found.Scopes.Count > 0 ? found.Scopes : scopes;
                break;
            case GitHubUserResult.Failed failed:
                return new GitHubSignInResult.Failed(failed.IsNetwork ? GitHubAuthProblem.Network : GitHubAuthProblem.Unexpected, failed.Detail);
            default:
                return new GitHubSignInResult.Failed(GitHubAuthProblem.Unexpected, "GitHub rejected the token it had just granted.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        GitHubAccount account;
        try
        {
            var saved = await _secrets.WriteAsync(TokenSecret, token, cancellationToken).ConfigureAwait(false);
            if (!saved.IsOk)
            {
                _logger.LogWarning("GitHub sign-in of {Login} not kept: the keychain couldn't save the token ({Problem})", user.Login, saved);
                return new GitHubSignInResult.NotSaved(user, saved);
            }

            account = GitHubAccount.From(user, scopes, _time.GetUtcNow());
            _accounts.Save(account);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Signed in to GitHub as {Login} ({Scopes}), token in {Keychain}", account.Login, string.Join(",", account.Scopes), _secrets.Kind);
        Changed?.Invoke(this, EventArgs.Empty);
        return new GitHubSignInResult.SignedIn(account);
    }

    /// <summary>The token for GitHub calls (tasks 60–63), or the keychain's problem / <see cref="SecretStatus.NotFound"/> when signed out.</summary>
    public Task<SecretResult> GetTokenAsync(CancellationToken cancellationToken = default) => _secrets.ReadAsync(TokenSecret, cancellationToken);

    /// <summary>Reads the token and asks GitHub who it belongs to; a rejected token signs out.</summary>
    public async Task<GitHubAccountCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var remembered = _accounts.Load();
        var token = await _secrets.ReadAsync(TokenSecret, cancellationToken).ConfigureAwait(false);
        switch (token.Status)
        {
            case SecretStatus.NotFound:
                if (remembered is not null)
                {
                    // The token was removed from the keychain by hand: nothing left to sign in with.
                    _logger.LogInformation("The GitHub token of {Login} is gone from the keychain; signed out", remembered.Login);
                    _accounts.Delete();
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                return new GitHubAccountCheck.SignedOut();
            case SecretStatus.Unavailable or SecretStatus.Failed:
                return new GitHubAccountCheck.KeychainProblem(remembered, token);
        }

        switch (await _users.GetUserAsync(token.Value!, cancellationToken).ConfigureAwait(false))
        {
            case GitHubUserResult.Found found:
                var account = GitHubAccount.From(found.User, found.Scopes.Count > 0 ? found.Scopes : remembered?.Scopes ?? [], remembered?.SignedInAt ?? _time.GetUtcNow());
                if (!Same(account, remembered))
                {
                    _accounts.Save(account);
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                return new GitHubAccountCheck.SignedIn(account);
            case GitHubUserResult.Unauthorized:
                _logger.LogWarning("GitHub rejected the token of {Login}: revoked; signing out", remembered?.Login);
                await SignOutAsync(cancellationToken).ConfigureAwait(false);
                return new GitHubAccountCheck.Revoked(remembered?.Login ?? string.Empty);
            case GitHubUserResult.Failed failed:
                return new GitHubAccountCheck.Unverified(remembered, failed.Detail);
            default:
                throw new InvalidOperationException("Unknown answer.");
        }
    }

    /// <summary>Deletes the token from the keychain and forgets the account; the keychain's result (Ok also when there was no token).</summary>
    public async Task<SecretResult> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SecretResult deleted;
        try
        {
            deleted = await _secrets.DeleteAsync(TokenSecret, cancellationToken).ConfigureAwait(false);
            if (deleted.IsOk)
            {
                _accounts.Delete();
            }
            else
            {
                _logger.LogWarning("Signing out of GitHub: the keychain couldn't delete the token ({Problem})", deleted);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    private static bool Same(GitHubAccount a, GitHubAccount? b) =>
        b is not null && a.Login == b.Login && a.Name == b.Name && a.AvatarUrl == b.AvatarUrl && a.ProfileUrl == b.ProfileUrl
        && a.Scopes.SequenceEqual(b.Scopes) && a.SignedInAt == b.SignedInAt;
}
