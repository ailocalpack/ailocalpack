using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Settings;

namespace AiLocalPack.Core.Tests.Activity;

/// <summary>Which Activity events become OS notifications (task 39).</summary>
public sealed class ActivityNotificationsTests
{
    private static readonly ActivityEvent Reapplied = ActivityEvent.Create(ActivityEventKind.PackAutoReapplied, DateTimeOffset.UnixEpoch);
    private static readonly ActivityEvent Incompatible = ActivityEvent.Create(ActivityEventKind.PackIncompatible, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Finished_task_notifies_while_the_window_is_not_in_front()
    {
        // Arrange
        var settings = AppSettings.Defaults;

        // Act
        var notify = ActivityNotifications.ShouldNotify(Reapplied, settings, windowActive: false);

        // Assert
        notify.Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_sent_while_the_window_is_in_front()
    {
        // Arrange
        var settings = AppSettings.Defaults;

        // Act
        var notify = ActivityNotifications.ShouldNotify(Incompatible, settings, windowActive: true);

        // Assert
        notify.Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_sent_with_the_tray_off()
    {
        // Arrange
        var settings = AppSettings.Defaults with { RunInBackground = false };

        // Act
        var notify = ActivityNotifications.ShouldNotify(Incompatible, settings, windowActive: false);

        // Assert
        notify.Should().BeFalse();
    }

    [Fact]
    public void Decision_follows_its_toggle()
    {
        // Arrange
        var settings = AppSettings.Defaults with { NotifyNeedsDecision = false };

        // Act
        var notify = (ActivityNotifications.ShouldNotify(Incompatible, settings, false), ActivityNotifications.ShouldNotify(Reapplied, settings, false));

        // Assert
        notify.Should().Be((false, true));
    }

    [Fact]
    public void Finished_task_follows_its_toggle()
    {
        // Arrange
        var settings = AppSettings.Defaults with { NotifyTaskFinished = false };

        // Act
        var notify = (ActivityNotifications.ShouldNotify(Incompatible, settings, false), ActivityNotifications.ShouldNotify(Reapplied, settings, false));

        // Assert
        notify.Should().Be((true, false));
    }

    [Fact]
    public void Failure_counts_as_a_finished_task()
    {
        // Arrange
        var failed = ActivityEvent.Create(ActivityEventKind.InstallFailed, DateTimeOffset.UnixEpoch);

        // Act
        var category = ActivityNotifications.CategoryOf(failed);

        // Assert
        category.Should().Be(NotificationCategory.TaskFinished);
    }

    [Fact]
    public void Hidden_event_is_not_sent()
    {
        // Arrange
        var hidden = Reapplied with { IsHidden = true };

        // Act
        var notify = ActivityNotifications.ShouldNotify(hidden, AppSettings.Defaults, windowActive: false);

        // Assert
        notify.Should().BeFalse();
    }
}
