using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Tests.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>The first-time publish dialog (task 61, design §7): GitHub sign-in and the license consent in one dialog.</summary>
public sealed class PublishSignInDialogTests
{
    private static PublishSignInDialogViewModel Show(ServiceProvider services, Action? onContinue = null) =>
        services.GetRequiredService<IDialogService>().Show<PublishSignInDialogViewModel>(new PublishSignInRequest(onContinue ?? (() => { })));

    private static Task Until(Func<bool> condition) => Wait.UntilAsync(condition);

    [Fact]
    public async Task Signed_out_it_starts_the_sign_in_and_waits_for_GitHub()
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var dialog = Show(services);
        await Until(() => dialog.SignIn.IsWaiting);

        // Assert
        (dialog.SignIn.UserCode, dialog.CanContinue, dialog.Hint).Should().Be(("WDJB-MJHT", false, "Waiting for GitHub…"));
    }

    [Fact]
    public async Task Approved_on_GitHub_it_waits_for_the_license_box()
    {
        // Arrange
        using var services = TestServices.Build();
        var dialog = Show(services);
        await Until(() => dialog.SignIn.IsWaiting);

        // Act
        services.GetRequiredService<FakeGitHubHost>().Approve(FakeGitHubHost.Octocat);
        await Until(() => dialog.IsSignedIn);

        // Assert
        (dialog.SignedInText, dialog.CanContinue, dialog.Hint).Should().Be(("Signed in as octocat", false, "Accept the license to continue"));
    }

    [Fact]
    public void Signed_in_and_ticked_Continue_is_on()
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        var dialog = Show(services);

        // Act
        dialog.IsLicenseAccepted = true;

        // Assert
        (dialog.ContinueCommand.CanExecute(null), dialog.Hint, services.GetRequiredService<FakeGitHubHost>().Starts).Should().Be((true, string.Empty, 0));
    }

    [Fact]
    public void An_accepted_license_comes_ticked()
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<SettingsStore>().Update(s => s with { PublishLicenseAccepted = true });

        // Act
        var dialog = Show(services);

        // Assert
        dialog.IsLicenseAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_closes_it_stops_the_sign_in_and_goes_nowhere()
    {
        // Arrange
        using var services = TestServices.Build();
        var continued = false;
        var dialog = Show(services, () => continued = true);
        await Until(() => dialog.SignIn.IsWaiting);

        // Act
        dialog.CancelCommand.Execute(null);
        await Until(() => services.GetRequiredService<FakeGitHubHost>().WasCancelled);

        // Assert
        (services.GetRequiredService<IDialogService>().Current, services.GetRequiredService<FakeGitHubHost>().WasCancelled, continued)
            .Should().Be((null, true, false));
    }
}
