using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.ViewModels.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GitHub;

public sealed class GitHubSignInViewModelTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose() => _services.Dispose();

    private FakeGitHubHost Host => _services.GetRequiredService<FakeGitHubHost>();

    private FakeShell Shell => _services.GetRequiredService<FakeShell>();

    private GitHubSignInViewModel Create() => _services.GetRequiredService<GitHubSignInViewModel>();

    /// <summary>Starts the sign-in and waits until the code is shown (the wait for the approval keeps running).</summary>
    private static async Task<Task> StartedAsync(GitHubSignInViewModel signIn)
    {
        var running = signIn.StartAsync();
        await Wait.UntilAsync(() => signIn.State != GitHubSignInState.Requesting);
        return running;
    }

    [Fact]
    public async Task Shows_the_code_and_where_to_enter_it()
    {
        // Arrange
        var signIn = Create();

        // Act
        await StartedAsync(signIn);

        // Assert
        (signIn.State, signIn.UserCode, signIn.StepOpenText, signIn.StatusText).Should().Be(
            (GitHubSignInState.Waiting, "WDJB-MJHT", "Open github.com/login/device in your browser", "Waiting for you to approve in the browser…"));
    }

    [Fact]
    public async Task Approval_reports_the_account_and_hides_the_panel()
    {
        // Arrange
        var signIn = Create();
        GitHubAccount? reported = null;
        signIn.SignedIn += (_, account) => reported = account;
        var running = await StartedAsync(signIn);

        // Act
        Host.Approve(FakeGitHubHost.Octocat);
        await running;

        // Assert
        (reported, signIn.State, signIn.IsActive).Should().Be((FakeGitHubHost.Octocat, GitHubSignInState.SignedIn, false));
    }

    [Fact]
    public async Task An_expired_code_says_so_and_offers_another_try()
    {
        // Arrange
        var signIn = Create();
        var running = await StartedAsync(signIn);

        // Act
        Host.Answer(new GitHubSignInResult.Expired());
        await running;

        // Assert
        (signIn.State, signIn.Error, signIn.StartCommand.CanExecute(null)).Should().Be(
            (GitHubSignInState.Failed, "The code expired before it was approved. Try again to get a new one.", true));
    }

    [Fact]
    public async Task Try_again_asks_for_a_new_code()
    {
        // Arrange
        var signIn = Create();
        var running = await StartedAsync(signIn);
        Host.Answer(new GitHubSignInResult.Denied());
        await running;

        // Act
        await StartedAsync(signIn);

        // Assert
        (Host.Starts, signIn.State, signIn.Error).Should().Be((2, GitHubSignInState.Waiting, (string?)null));
    }

    [Fact]
    public async Task Github_out_of_reach_fails_before_a_code()
    {
        // Arrange
        Host.Start = new DeviceCodeResult.Failed(GitHubAuthProblem.Network, "No such host");
        var signIn = Create();

        // Act
        await signIn.StartAsync();

        // Assert
        (signIn.State, signIn.Error).Should().Be((GitHubSignInState.Failed, "Can't reach GitHub. Check the connection and try again."));
    }

    [Fact]
    public async Task A_keychain_that_cant_keep_the_token_leaves_the_player_signed_out_and_says_why()
    {
        // Arrange
        var signIn = Create();
        var running = await StartedAsync(signIn);
        var user = new GitHubUser("octocat", null, null, new Uri("https://github.com/octocat"));

        // Act
        Host.Answer(new GitHubSignInResult.NotSaved(user, SecretResult.Unavailable("no Secret Service on the session bus")));
        await running;

        // Assert
        signIn.Error.Should().Be(
            "GitHub approved octocat, but macOS Keychain couldn't keep the token, so you stay signed out (it is never saved in plain text): no Secret Service on the session bus");
    }

    [Fact]
    public async Task Cancel_stops_waiting_and_hides_the_panel()
    {
        // Arrange
        var signIn = Create();
        var running = await StartedAsync(signIn);

        // Act
        signIn.CancelCommand.Execute(null);
        await running;

        // Assert
        (signIn.State, signIn.IsActive, Host.WasCancelled).Should().Be((GitHubSignInState.Idle, false, true));
    }

    [Fact]
    public async Task An_approval_after_cancel_is_dropped()
    {
        // Arrange
        var signIn = Create();
        var reported = false;
        signIn.SignedIn += (_, _) => reported = true;
        var running = await StartedAsync(signIn);
        signIn.Cancel();
        await running;

        // Act
        Host.Approve(FakeGitHubHost.Octocat);

        // Assert
        (reported, signIn.State).Should().Be((false, GitHubSignInState.Idle));
    }

    [Fact]
    public async Task Copy_puts_the_code_on_the_clipboard()
    {
        // Arrange
        var signIn = Create();
        await StartedAsync(signIn);

        // Act
        await signIn.CopyCommand.ExecuteAsync(null);

        // Assert
        (Shell.Copied.Single(), signIn.CopyText).Should().Be(("WDJB-MJHT", "Copied"));
    }

    [Fact]
    public async Task Open_browser_opens_githubs_device_page()
    {
        // Arrange
        var signIn = Create();
        await StartedAsync(signIn);

        // Act
        signIn.OpenBrowserCommand.Execute(null);

        // Assert
        Shell.OpenedUrls.Should().Equal(new Uri("https://github.com/login/device"));
    }
}
