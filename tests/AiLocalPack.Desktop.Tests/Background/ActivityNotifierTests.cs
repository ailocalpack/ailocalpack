using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Background;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Background;

/// <summary>Activity events as OS notifications, routed by the General → Notifications settings (task 39).</summary>
public sealed class ActivityNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Services() =>
        TestServices.Build(s => s.AddSingleton<TimeProvider>(new FakeClock(Now)), steam => steam.AddGame(10, "Alpha"));

    private static ActivityEvent Event(ActivityEventKind kind) =>
        ActivityEvent.Create(kind, Now) with { Game = GameId.Steam(10), GameName = "Alpha", PackId = "lanterns", PackTitle = "Фонарщики", Revision = 2 };

    private static FakeNotifications Start(IServiceProvider services)
    {
        services.GetRequiredService<ActivityNotifier>();
        return services.GetRequiredService<FakeNotifications>();
    }

    [Fact]
    public void Event_while_the_window_is_away_becomes_a_notification()
    {
        // Arrange
        using var services = Services();
        var notifications = Start(services);
        var activity = Event(ActivityEventKind.PackAutoReapplied);

        // Act
        services.GetRequiredService<ActivityStore>().Add(activity);

        // Assert
        notifications.Shown.Should().Equal(new SystemNotification(activity.Id, "«Фонарщики» re-applied after the game update", "Alpha"));
    }

    [Fact]
    public void Decision_body_carries_its_detail_line()
    {
        // Arrange
        using var services = Services();
        var notifications = Start(services);

        // Act
        services.GetRequiredService<ActivityStore>().Add(Event(ActivityEventKind.PackIncompatible));

        // Assert
        notifications.Shown.Single().Body.Should().Be("Alpha · No newer revision yet: translate the new strings with AI.");
    }

    [Fact]
    public void Turned_off_category_sends_nothing()
    {
        // Arrange
        using var services = Services();
        var notifications = Start(services);
        services.GetRequiredService<SettingsStore>().Update(s => s with { NotifyTaskFinished = false });

        // Act
        services.GetRequiredService<ActivityStore>().Add(Event(ActivityEventKind.PackAutoReapplied));

        // Assert
        notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    public void Known_finding_added_again_is_not_sent_twice()
    {
        // Arrange
        using var services = Services();
        var notifications = Start(services);
        var store = services.GetRequiredService<ActivityStore>();
        store.Add(Event(ActivityEventKind.PackIncompatible) with { Key = "finding" });

        // Act
        store.Add(Event(ActivityEventKind.PackIncompatible) with { Key = "finding" });

        // Assert
        notifications.Shown.Should().ContainSingle();
    }

    [Fact]
    public void Click_shows_the_window_on_the_game_page()
    {
        // Arrange
        using var services = Services();
        var notifications = Start(services);
        var activity = Event(ActivityEventKind.PackAutoReapplied);
        services.GetRequiredService<ActivityStore>().Add(activity);

        // Act
        notifications.Click(activity.Id);

        // Assert
        (services.GetRequiredService<FakeAppWindow>().Shows,
            services.GetRequiredService<INavigationService>().Current is GamePageViewModel { IsPacksTab: true } page && page.Game.Id == GameId.Steam(10),
            services.GetRequiredService<ActivityStore>().Find(activity.Id)!.IsRead)
            .Should().Be((1, true, true));
    }
}
