using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.ViewModels.GitHub;
using AiLocalPack.Desktop.ViewModels.Settings.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GitHub;

public sealed class GitHubSettingsSectionTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose() => _services.Dispose();

    private FakeGitHubHost Host => _services.GetRequiredService<FakeGitHubHost>();

    /// <summary>The section, created after the test set GitHub up.</summary>
    private GitHubSettingsSection Section() => _services.GetRequiredService<GitHubSettingsSection>();

    /// <summary>Opens the section (it asks GitHub) and waits for the answer.</summary>
    private async Task<GitHubAccountRow> OpenedAsync()
    {
        var section = Section();
        await section.Account.CheckAsync();
        return section.Account;
    }

    [Fact]
    public void Signed_out_explains_why_an_account_is_needed_and_is_never_a_warning()
    {
        // Act
        var section = Section();

        // Assert
        (section.Account.Title, section.Account.Help, section.Account.CanSignIn, section.BadgeCount).Should().Be(
            ("Not signed in", "Only needed to publish translations and to rate or confirm them. Installing never needs an account.", true, 0));
    }

    [Fact]
    public void Shows_the_remembered_account_without_asking_github()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;

        // Act
        var row = Section().Account;

        // Assert
        (row.Title, row.FullName, row.AvatarLetter, row.CanSignOut, Host.Checks).Should().Be(("octocat", "The Octocat", "O", true, 0));
    }

    [Fact]
    public void Signed_in_names_where_the_token_is_kept()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;

        // Act
        var help = Section().Account.Help;

        // Assert
        help.Should().Be("Signed in · token kept in the system keychain (macOS Keychain)");
    }

    [Fact]
    public void Opening_the_section_asks_github()
    {
        // Arrange
        var section = Section();

        // Act
        section.OnShown();

        // Assert
        Host.Checks.Should().Be(1);
    }

    [Fact]
    public async Task A_revoked_token_signs_out_and_says_so()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;
        Host.Check = () => new GitHubAccountCheck.Revoked("octocat");

        // Act
        var row = await OpenedAsync();

        // Assert
        (row.IsSignedIn, row.Help, row.CanSignIn).Should().Be(
            (false, "GitHub no longer accepts the app's access for octocat, so the app signed out. Sign in again to publish or rate translations.", true));
    }

    [Fact]
    public async Task Github_out_of_reach_keeps_the_remembered_account_with_a_note()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;
        Host.Check = () => new GitHubAccountCheck.Unverified(FakeGitHubHost.Octocat, "No such host");

        // Act
        var row = await OpenedAsync();

        // Assert
        (row.IsSignedIn, row.Warning).Should().Be((true, "GitHub can't be reached right now; showing the account as last seen."));
    }

    [Fact]
    public async Task A_keychain_that_cant_be_read_says_so_and_offers_check_again()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;
        Host.Check = () => new GitHubAccountCheck.KeychainProblem(FakeGitHubHost.Octocat, SecretResult.Failed("the unlock prompt was dismissed"));

        // Act
        var row = await OpenedAsync();

        // Assert
        (row.Warning, row.CanCheckAgain).Should().Be(("Can't read the token from macOS Keychain: the unlock prompt was dismissed", true));
    }

    [Fact]
    public async Task Sign_in_shows_the_device_flow_inline_instead_of_the_button()
    {
        // Arrange
        var row = Section().Account;

        // Act
        _ = row.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => row.SignIn.IsWaiting);

        // Assert
        (row.SignIn.UserCode, row.CanSignIn).Should().Be(("WDJB-MJHT", false));
    }

    [Fact]
    public async Task An_approved_sign_in_shows_the_account()
    {
        // Arrange
        var section = Section();
        var signingIn = section.Account.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => section.Account.SignIn.IsWaiting);

        // Act
        Host.Approve(FakeGitHubHost.Octocat);
        await signingIn;

        // Assert
        (section.Account.Title, section.Account.SignIn.IsActive, section.PublishedPacks.Text).Should().Be(
            ("octocat", false, "Nothing published yet. After creating a translation with AI, publish it from the game's Create with AI tab."));
    }

    [Fact]
    public async Task Sign_out_forgets_the_account_and_points_to_revoking_on_github()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;
        var row = Section().Account;

        // Act
        await row.SignOutCommand.ExecuteAsync(null);

        // Assert
        (row.IsSignedIn, row.HelpLinkText, row.HelpLinkUri).Should().Be(
            (false, "Revoke access on GitHub", new Uri("https://github.com/settings/connections/applications/" + GitHubOAuthApp.DefaultClientId)));
    }

    [Fact]
    public async Task A_keychain_that_refuses_to_delete_keeps_the_player_signed_in()
    {
        // Arrange
        Host.Account = FakeGitHubHost.Octocat;
        Host.SignOut = SecretResult.Failed("access denied");
        var row = Section().Account;

        // Act
        await row.SignOutCommand.ExecuteAsync(null);

        // Assert
        (row.IsSignedIn, row.Error).Should().Be((true, "Can't delete the token from macOS Keychain, so you are still signed in: access denied"));
    }

    [Fact]
    public void Follows_a_sign_in_elsewhere_in_the_app()
    {
        // Arrange
        var row = Section().Account;

        // Act
        Host.ChangeElsewhere(FakeGitHubHost.Octocat);

        // Assert
        row.Title.Should().Be("octocat");
    }

    [Fact]
    public void Signed_out_published_packs_ask_to_sign_in()
    {
        // Act
        var text = Section().PublishedPacks.Text;

        // Assert
        text.Should().Be("Sign in to see translations you published.");
    }

    [Fact]
    public async Task Opening_the_section_while_signing_in_doesnt_check()
    {
        // Arrange
        var section = Section();
        _ = section.Account.SignInCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => section.Account.SignIn.State == GitHubSignInState.Waiting);

        // Act
        section.OnShown();

        // Assert
        Host.Checks.Should().Be(0);
    }
}
