using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Activity;

/// <summary>The 🔔 badge and the Activity popover in the window (design §5): sections, keyboard, Esc.</summary>
public sealed class ActivityPanelViewTests
{
    private static (MainWindow Window, ActivityStore Store) Open(Action<ActivityStore>? seed = null)
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        var store = services.GetRequiredService<ActivityStore>();
        seed?.Invoke(store);
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        window.Closed += (_, _) => services.Dispose();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, store);
    }

    private static ActivityEvent Event(string id, ActivityEventKind kind = ActivityEventKind.PackInstalled) =>
        ActivityEvent.Create(kind, DateTimeOffset.UtcNow.AddMinutes(-1)) with
        {
            Id = id,
            Game = GameId.Steam(10),
            GameName = "Alpha",
            PackId = "lanterns",
            PackTitle = "lanterns",
            Revision = 1,
        };

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Button Bell(MainWindow window) => Find<Button>(window, "ActivityButton");

    private static ActivityPanelView PanelView(MainWindow window) => (ActivityPanelView)((Flyout)Bell(window).Flyout!).Content!;

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Badge_is_amber_with_the_decisions() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, store) = Open();

        // Act
        store.Add(Event("broken", ActivityEventKind.AutoReapplyFailed));
        Dispatcher.UIThread.RunJobs();

        // Assert
        var badge = Find<Border>(window, "ActivityBadge");
        (badge.IsVisible, badge.Classes.Contains("attention"), AutomationProperties.GetName(Bell(window)))
            .Should().Be((true, true, "Activity: 1 needs your decision"));
        window.Close();
    });

    [Fact]
    public Task Bell_opens_the_decision_card() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open(s => s.Add(Event("broken", ActivityEventKind.PackIncompatible)));

        // Act
        Click(window, Bell(window));

        // Assert
        var rows = PanelView(window).Rows;
        (((Flyout)Bell(window).Flyout!).IsOpen, rows.Count, rows[0].FindAncestorOfType<Border>()!.Classes.Contains("decision")).Should().Be((true, 1, true));
        window.Close();
    });

    [Fact]
    public Task Down_moves_to_the_next_row() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open(s =>
        {
            s.Add(Event("broken", ActivityEventKind.PackIncompatible));
            s.Add(Event("installed"));
        });
        Click(window, Bell(window));
        var panel = PanelView(window);
        panel.Rows[0].Focus();

        // Act
        panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Dispatcher.UIThread.RunJobs();

        // Assert
        panel.Rows[1].IsFocused.Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Escape_closes_the_panel() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open(s => s.Add(Event("installed")));
        Click(window, Bell(window));

        // Act
        PanelView(window).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();

        // Assert
        ((Flyout)Bell(window).Flyout!).IsOpen.Should().BeFalse();
        window.Close();
    });
}
