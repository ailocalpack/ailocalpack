using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.GitHub;
using Avalonia.Media.Imaging;

namespace AiLocalPack.Desktop.Tests.GitHub;

/// <summary>
/// GitHub as the test sets it, so no test reaches github.com or the machine's keychain. Signed out by default; GitHub
/// answers with a code at once and then waits for the test: <see cref="Approve"/> / <see cref="Answer"/> end the wait.
/// <see cref="Check"/> and <see cref="SignOut"/> decide those calls.
/// </summary>
internal sealed class FakeGitHubHost : IGitHubHost
{
    private TaskCompletionSource<GitHubSignInResult> _finish = NewFinish();

    public static GitHubAccount Octocat { get; } = new(
        "octocat", "The Octocat", new Uri("https://avatars.example/u/1"), new Uri("https://github.com/octocat"), ["public_repo"], DateTimeOffset.UnixEpoch);

    public static DeviceAuthorization Code { get; } = new(
        "device-code", "WDJB-MJHT", new Uri("https://github.com/login/device"), DateTimeOffset.UnixEpoch.AddMinutes(15), TimeSpan.FromSeconds(5));

    public event EventHandler? Changed;

    public GitHubOAuthApp App { get; } = GitHubOAuthApp.Default;

    public SecretStoreKind KeychainKind { get; set; } = SecretStoreKind.MacKeychain;

    public GitHubAccount? Account { get; set; }

    /// <summary>GitHub's answer to "give me a code". Default: <see cref="Code"/>.</summary>
    public DeviceCodeResult Start { get; set; } = new DeviceCodeResult.Started(Code);

    /// <summary>What a check finds. Default: signed in as <see cref="Account"/> when there is one, else signed out.</summary>
    public Func<GitHubAccountCheck>? Check { get; set; }

    /// <summary>The keychain's answer to a sign-out. Default: Ok.</summary>
    public SecretResult SignOut { get; set; } = SecretResult.Ok();

    public int Starts { get; private set; }

    public int Checks { get; private set; }

    public bool WasCancelled { get; private set; }

    public Task<DeviceCodeResult> StartSignInAsync(CancellationToken cancellationToken = default)
    {
        Starts++;
        _finish = NewFinish();
        return Task.FromResult(Start);
    }

    public async Task<GitHubSignInResult> FinishSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _finish.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            throw;
        }
    }

    /// <summary>The player approved: the account is kept, as the real session does, and the wait ends.</summary>
    public void Approve(GitHubAccount account)
    {
        Account = account;
        Changed?.Invoke(this, EventArgs.Empty);
        _finish.TrySetResult(new GitHubSignInResult.SignedIn(account));
    }

    /// <summary>Ends the wait with <paramref name="result"/> (expired, denied, failed, not saved).</summary>
    public void Answer(GitHubSignInResult result) => _finish.TrySetResult(result);

    public Task<GitHubAccountCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return Task.FromResult(Check?.Invoke() ?? (Account is { } account ? new GitHubAccountCheck.SignedIn(account) : new GitHubAccountCheck.SignedOut()));
    }

    public Task<SecretResult> SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (SignOut.IsOk)
        {
            Account = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(SignOut);
    }

    /// <summary>Signs in or out somewhere else in the app.</summary>
    public void ChangeElsewhere(GitHubAccount? account)
    {
        Account = account;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<Bitmap?> LoadAvatarAsync(Uri avatar, CancellationToken cancellationToken = default) => Task.FromResult<Bitmap?>(null);

    private static TaskCompletionSource<GitHubSignInResult> NewFinish() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
