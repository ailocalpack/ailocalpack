using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The result card, the Fix with AI thread with its docked composer, and Packs → Mine rendered headless at 1280×800 (tasks 55, 75, 76).</summary>
public sealed class GenerationResultViewTests
{
    private static (MainWindow Window, GamePageViewModel Page) Open(InstallBed bed, GamePageTab tab)
    {
        var window = new MainWindow { DataContext = bed.Services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        var page = bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, tab);
        bed.Services.GetRequiredService<INavigationService>().NavigateTo(page);
        Dispatcher.UIThread.RunJobs();
        return (window, page);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task Result_card_shows_the_tiles_and_install_and_play() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        (Find<Border>(window, "ResultCard").IsEffectivelyVisible,
            Find<ItemsControl>(window, "ResultTiles").ItemCount,
            Find<Button>(window, "ResultPlay").IsEffectivelyEnabled,
            Find<Button>(window, "ResultDiscard").IsEffectivelyVisible,
            Find<StackPanel>(window, "ShareStrip").IsVisible)
            .Should().Be((true, 4, true, true, false));
        window.Close();
    });

    [Fact]
    public Task Tried_result_shows_the_share_strip() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero));

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        (Find<StackPanel>(window, "ShareStrip").IsEffectivelyVisible, Find<Button>(window, "SharePublish").IsEffectivelyEnabled, Find<Button>(window, "ResultShowInPacks").IsEffectivelyVisible)
            .Should().Be((true, true, true));
        window.Close();
    });

    [Fact]
    public Task Mine_shows_the_local_pack_with_its_details() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var (window, _) = Open(bed, GamePageTab.Packs);

        // Assert
        (Find<ItemsControl>(window, "MineList").ItemCount, Find<TextBlock>(window, "MineTitle").Text, Find<TextBlock>(window, "MineCoverage").Text,
            Find<Button>(window, "MineDelete").IsEffectivelyEnabled)
            .Should().Be((1, "Claude Code translation", "100% of strings", true));
        window.Close();
    });

    [Fact]
    public Task The_fix_card_shows_the_conversation_and_the_composer() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        var folder = bed.Services.GetRequiredService<Desktop.Generation.GenerationService>().ResultOf(InstallBed.Alpha)!.Folder;
        var at = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
        new Core.Generation.FixFiles(folder).Write(new Core.Generation.FixState(
            [
                Core.Generation.FixTurn.FromPlayer(1, at, "Use «вы».", [Core.Generation.FixAttachment.Changes()]),
                new Core.Generation.FixTurn(2, Core.Generation.FixTurnKind.Agent, at.AddMinutes(1), "That touches 60 strings.", [], ["search"], [],
                    Core.Generation.FixTurnOutcome.Planned, Plan: new Core.Generation.FixPlan(60, 0, false, [new Core.Generation.FixStringChange("a", "Hi", "Привет", "Здравствуйте")])),
            ],
            [],
            1));

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        (Find<StackPanel>(window, "FixCard").IsEffectivelyVisible, Find<ItemsControl>(window, "FixThread").ItemCount, Find<TextBox>(window, "FixRequest").IsEffectivelyEnabled,
            Find<Button>(window, "FixSend").IsEffectivelyVisible, Find<ItemsControl>(window, "ResultTiles").IsVisible)
            .Should().Be((true, 3, true, true, false));
        window.Close();
    });

    private static void Conversation(InstallBed bed, int rounds)
    {
        var folder = bed.Services.GetRequiredService<Desktop.Generation.GenerationService>().ResultOf(InstallBed.Alpha)!.Folder;
        var at = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
        var turns = Enumerable.Range(0, rounds).SelectMany(i => new[]
        {
            Core.Generation.FixTurn.FromPlayer(i * 2 + 1, at.AddMinutes(i * 10), $"Request {i + 1}", [Core.Generation.FixAttachment.Changes()]),
            new Core.Generation.FixTurn(i * 2 + 2, Core.Generation.FixTurnKind.Agent, at.AddMinutes(i * 10 + 1), $"Answer {i + 1}", [], ["search"], [],
                Core.Generation.FixTurnOutcome.Answered),
        }).ToList();
        new Core.Generation.FixFiles(folder).Write(new Core.Generation.FixState(turns, [], 1));
    }

    [Fact]
    public Task The_composer_is_docked_at_the_bottom_of_the_page() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        Conversation(bed, rounds: 1);

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        var request = Find<TextBox>(window, "FixRequest");
        (Find<Border>(window, "ComposerDock").IsEffectivelyVisible,
            Find<Border>(window, "ComposerStub").IsEffectivelyVisible,
            request.TranslatePoint(new Avalonia.Point(0, request.Bounds.Height), window)!.Value.Y > 800 - 160)
            .Should().Be((true, false, true));
        window.Close();
    });

    [Fact]
    public Task A_long_conversation_folds_its_earlier_rounds() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        Conversation(bed, rounds: 4);
        var (window, page) = Open(bed, GamePageTab.Generation);
        var folded = (Find<Button>(window, "FixEarlier").IsEffectivelyVisible, Find<ItemsControl>(window, "FixThread").ItemCount);

        // Act
        page.Generation.Fix!.ShowEarlierCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (folded, Find<Button>(window, "FixEarlier").IsVisible, Find<ItemsControl>(window, "FixThread").ItemCount)
            .Should().Be(((true, 4), false, 9));
        window.Close();
    });

    [Fact]
    public Task The_agent_reply_shows_its_markdown_formatted() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        var folder = bed.Services.GetRequiredService<Desktop.Generation.GenerationService>().ResultOf(InstallBed.Alpha)!.Folder;
        var at = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
        new Core.Generation.FixFiles(folder).Write(new Core.Generation.FixState(
            [
                Core.Generation.FixTurn.FromPlayer(1, at, "What did you change?", []),
                new Core.Generation.FixTurn(2, Core.Generation.FixTurnKind.Agent, at.AddMinutes(1), "**Changed:**\n- `menu.start`\n- `menu.quit`", [], ["search"], [],
                    Core.Generation.FixTurnOutcome.Answered),
            ],
            [],
            1));

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        var reply = Find<MarkdownView>(window, "FixAgentText");
        (reply.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("mdList")), Avalonia.Automation.AutomationProperties.GetName(reply))
            .Should().Be((1, "Changed:\n• menu.start\n• menu.quit"));
        window.Close();
    });
}
