using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>The publish dialogs and strip rendered headless at 1280×800 (task 61).</summary>
public sealed class PublishViewTests
{
    private static readonly DateTimeOffset Tried = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);

    private static MainWindow Open(InstallBed bed, GamePageTab tab)
    {
        var window = new MainWindow { DataContext = bed.Services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        bed.Services.GetRequiredService<INavigationService>().NavigateTo(bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, tab));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task Publish_on_the_share_strip_opens_the_first_time_dialog() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: Tried);
        var window = Open(bed, GamePageTab.Generation);

        // Act
        Find<Button>(window, "SharePublish").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<TextBlock>(window, "PublishSignInTitle").Text, Find<Button>(window, "ContinueButton").IsEffectivelyEnabled,
            Find<SelectableTextBlock>(window, "UserCode").Text, Find<TextBlock>(window, "PublishHint").Text)
            .Should().Be(("Publish to the ailocalpack registry", false, "WDJB-MJHT", "Waiting for GitHub…"));
        window.Close();
    });

    [Fact]
    public Task The_similar_pack_dialog_shows_the_registry_pack_and_the_recommended_choice() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.Registry.Pack(10, "ru", "neon", likes: 42, translated: 5));
        bed.LocalPack(tried: Tried);
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { PublishLicenseAccepted = true });
        var window = Open(bed, GamePageTab.Generation);

        // Act
        Find<Button>(window, "SharePublish").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<TextBlock>(window, "ExistingTitle").Text, Find<Border>(window, "Recommended").IsEffectivelyVisible,
            Find<RadioButton>(window, "RevisionChoice").IsChecked, Find<RadioButton>(window, "SeparateChoice").IsChecked)
            .Should().Be(("«neon» by alice · rev 1", true, true, false));
        window.Close();
    });

    [Fact]
    public Task Once_sent_the_strip_and_Mine_show_CI_running() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        var window = Open(bed, GamePageTab.Generation);

        // Act
        await bed.Services.GetRequiredService<PublishFlow>().SendAsync(bed.Game, pack, new PublishTarget.NewPack());
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<Border>(window, "ShareStrip").IsEffectivelyVisible, Find<TextBlock>(window, "PublishTitle").Text, Find<Button>(window, "PublishOpenPr").IsEffectivelyVisible)
            .Should().Be((false, "PR #7 opened · CI is checking the translation", true));
        window.Close();
    });

    private static async Task RejectedAsync(InstallBed bed)
    {
        bed.LocalPack(tried: Tried);
        bed.Services.GetRequiredService<Core.Packs.LocalPackStore>().UpdateReport(InstallBed.Alpha, "ru", "claude-code", r => r with
        {
            Published = new Core.Packs.LocalPackPublication("ailocalpack/registry-test", "claude-code", 1, 7, FakePackPublishing.PrUrl, "octocat/registry-test", "b", Tried),
        });
        bed.Services.GetRequiredService<GenerationService>().Recover();
        bed.Services.GetRequiredService<FakePullRequestStatus>().Answer(7, PullRequestState.Open, CiState.Failed, "h1",
            new CiAnnotation(null, CiAnnotationLevel.Failure, CiFailures.ScopeTitle, "This pull request can't be accepted: README.md is outside games/."));
        await bed.Services.GetRequiredService<PullRequestTracker>().CheckAsync(all: true);
    }

    [Fact]
    public Task A_rejected_pr_shows_the_reason_and_Fix_and_resubmit() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await RejectedAsync(bed);

        // Act
        var window = Open(bed, GamePageTab.Generation);

        // Assert
        (Find<TextBlock>(window, "PublishTitle").Text, Find<TextBlock>(window, "PublishText").Text,
                Find<Button>(window, "PublishResubmit").IsEffectivelyVisible, Find<Button>(window, "PublishAgain").IsEffectivelyVisible)
            .Should().Be(("CI rejected PR #7", "The pull request changes files outside the translation's folder. Fix and resubmit rebuilds its branch with only this translation.", true, false));
        window.Close();
    });

    [Fact]
    public Task My_published_packs_lists_the_rejected_pr_with_Fix_and_resubmit() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await RejectedAsync(bed);
        var window = new MainWindow { DataContext = bed.Services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();

        // Act
        bed.Services.GetRequiredService<INavigationService>().NavigateTo(bed.Services.GetRequiredService<IPageFactory>().Create<SettingsViewModel>(ViewModels.Settings.SettingsSectionId.GitHub));
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<TextBlock>(window, "PublishedPackStatus").Text, Find<Button>(window, "PublishedPackResubmit").IsEffectivelyVisible, Find<TextBlock>(window, "PublishedPacksEmpty").IsEffectivelyVisible)
            .Should().Be(("PR #7 · CI failed", true, false));
        window.Close();
    });
}
