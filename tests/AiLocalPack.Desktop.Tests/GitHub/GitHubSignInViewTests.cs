using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GitHub;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GitHub;

public sealed class GitHubSignInViewTests
{
    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Button button)
    {
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The panel alone in a window, as the publish dialog (task 61) will host it.</summary>
    private static (Window Window, GitHubSignInViewModel SignIn) Show(ServiceProvider services)
    {
        var signIn = services.GetRequiredService<GitHubSignInViewModel>();
        var window = new Window { Content = new GitHubSignInView { DataContext = signIn }, Width = 640, Height = 320 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, signIn);
    }

    private static async Task RunUntilAsync(Func<bool> condition)
    {
        await Wait.UntilAsync(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return condition();
        });
    }

    [Fact]
    public Task Is_hidden_until_the_host_starts_it() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var (window, _) = Show(services);

        // Assert
        window.GetVisualDescendants().OfType<GitHubSignInView>().Single().IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Shows_the_code_with_copy_and_open_browser() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = TestServices.Build();
        var (window, signIn) = Show(services);

        // Act
        _ = signIn.StartAsync();
        await RunUntilAsync(() => signIn.IsWaiting);

        // Assert
        (Find<SelectableTextBlock>(window, "UserCode").Text, Find<TextBlock>(window, "StepOpen").Text,
                Find<Button>(window, "CopyCode").IsEffectivelyVisible, Find<Button>(window, "OpenBrowser").IsEffectivelyVisible)
            .Should().Be(("WDJB-MJHT", "Open github.com/login/device in your browser", true, true));
        window.Close();
    });

    [Fact]
    public Task Copy_button_copies_the_code() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = TestServices.Build();
        var (window, signIn) = Show(services);
        _ = signIn.StartAsync();
        await RunUntilAsync(() => signIn.IsWaiting);

        // Act
        Click(Find<Button>(window, "CopyCode"));
        await RunUntilAsync(() => signIn.CopyText == "Copied");

        // Assert
        services.GetRequiredService<FakeShell>().Copied.Should().Equal("WDJB-MJHT");
        window.Close();
    });

    [Fact]
    public Task Cancel_button_hides_the_panel() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = TestServices.Build();
        var (window, signIn) = Show(services);
        _ = signIn.StartAsync();
        await RunUntilAsync(() => signIn.IsWaiting);

        // Act
        Click(Find<Button>(window, "CancelSignIn"));

        // Assert
        window.GetVisualDescendants().OfType<GitHubSignInView>().Single().IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task A_failure_shows_the_reason_and_try_again() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<FakeGitHubHost>().Start =
            new Core.GitHub.DeviceCodeResult.Failed(Core.GitHub.GitHubAuthProblem.Network, "offline");
        var (window, signIn) = Show(services);

        // Act
        await signIn.StartAsync();
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<TextBlock>(window, "SignInError").Text, Find<Button>(window, "TryAgain").IsEffectivelyVisible)
            .Should().Be(("Can't reach GitHub. Check the connection and try again.", true));
        window.Close();
    });

    [Fact]
    public Task Settings_github_signs_in_inline() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        services.GetRequiredService<INavigationService>().NavigateTo(services.GetRequiredService<IPageFactory>().Create<SettingsViewModel>(SettingsSectionId.GitHub));
        window.Show();
        await RunUntilAsync(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "GitHubSignIn"));

        // Act
        Click(Find<Button>(window, "GitHubSignIn"));
        await RunUntilAsync(() => window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Name == "UserCode" && t.Text == "WDJB-MJHT"));
        services.GetRequiredService<FakeGitHubHost>().Approve(FakeGitHubHost.Octocat);
        await RunUntilAsync(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "GitHubSignOut" && b.IsEffectivelyVisible)
            && !Find<GitHubSignInView>(window, "GitHubDeviceFlow").IsVisible);

        // Assert
        (Find<HighlightTextBlock>(window, "GitHubLogin").Source, Find<GitHubSignInView>(window, "GitHubDeviceFlow").IsVisible,
                Find<Button>(window, "GitHubSignIn").IsVisible, Find<TextBlock>(window, "PublishedPacksEmpty").Text)
            .Should().Be(("octocat", false, false, "Nothing published yet. After creating a translation with AI, publish it from the game's Create with AI tab."));
        window.Close();
    });
}
