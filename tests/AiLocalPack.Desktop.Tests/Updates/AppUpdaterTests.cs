using AiLocalPack.Core.Activity;
using AiLocalPack.Desktop.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Updates;

/// <summary>The app's self-update state machine (task 67) over a fake release feed.</summary>
public sealed class AppUpdaterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 14, 5, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services = TestServices.Build();
    private readonly FakeUpdateSource _source = new();
    private readonly FakeAppQuit _quit = new();
    private readonly TimerClock _clock = new(Now);

    public void Dispose() => _services.Dispose();

    private ActivityStore Activity => _services.GetRequiredService<ActivityStore>();

    private AppUpdater Updater() => new(_source, _quit, Activity, _clock, NullLogger<AppUpdater>.Instance);

    /// <summary>An updater that found <paramref name="version"/> and downloaded it.</summary>
    private async Task<AppUpdater> ReadyAsync(string version = "0.5.0")
    {
        _source.Offer = version;
        var updater = Updater();
        await updater.CheckAsync(TestContext.Current.CancellationToken);
        await updater.Download;
        return updater;
    }

    [Fact]
    public async Task Development_build_is_off_and_never_checks()
    {
        // Arrange
        _source.Install = AppInstallKind.Development;
        var updater = Updater();

        // Act
        var status = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (status, updater.Status, _source.Checks).Should().Be((AppUpdateStatus.Off, AppUpdateStatus.Off, 0));
    }

    [Fact]
    public void Update_downloaded_before_starts_ready()
    {
        // Arrange
        _source.PendingRestart = new AppUpdateOffer("0.5.0", null);

        // Act
        var updater = Updater();

        // Assert
        (updater.Status, updater.Version).Should().Be((AppUpdateStatus.Ready, "0.5.0"));
    }

    [Fact]
    public async Task Nothing_newer_is_up_to_date_with_the_time_checked()
    {
        // Arrange
        var updater = Updater();

        // Act
        var status = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (status, updater.Status, updater.CheckedAt, updater.Version).Should().Be((AppUpdateStatus.UpToDate, AppUpdateStatus.UpToDate, Now, (string?)null));
    }

    [Fact]
    public async Task Newer_release_starts_downloading()
    {
        // Arrange
        _source.Offer = "0.5.0";
        _source.DownloadGate = new TaskCompletionSource();
        var updater = Updater();

        // Act
        var status = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (status, updater.Status, updater.Version).Should().Be((AppUpdateStatus.Downloading, AppUpdateStatus.Downloading, "0.5.0"));
        _source.DownloadGate.SetResult();
    }

    [Fact]
    public async Task Download_reports_its_progress()
    {
        // Arrange
        _source.Offer = "0.5.0";
        _source.DownloadProgress = [10, 30];
        _source.DownloadGate = new TaskCompletionSource();
        var updater = Updater();
        var progressed = new TaskCompletionSource();
        updater.Changed += (_, _) =>
        {
            if (updater.Percent == 30)
            {
                progressed.TrySetResult();
            }
        };

        // Act
        await updater.CheckAsync(TestContext.Current.CancellationToken);
        await progressed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        (updater.Status, updater.Percent).Should().Be((AppUpdateStatus.Downloading, 30));
        _source.DownloadGate.SetResult();
    }

    [Fact]
    public async Task Finished_download_is_ready_to_restart()
    {
        // Arrange
        _source.Offer = "0.5.0";
        var updater = Updater();
        await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Act
        await updater.Download;

        // Assert
        (updater.Status, updater.Version, updater.Percent, _source.Downloaded.Single()).Should().Be((AppUpdateStatus.Ready, "0.5.0", 100, "0.5.0"));
    }

    [Fact]
    public async Task Ready_update_goes_to_the_activity_feed()
    {
        // Arrange
        _source.Offer = "0.5.0";
        var updater = Updater();
        await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Act
        await updater.Download;

        // Assert
        Activity.Feed.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = ActivityEventKind.AppUpdateReady,
            Version = "0.5.0",
            Key = "app-update:0.5.0",
            Game = (object?)null,
            IsDecision = false,
        });
    }

    [Fact]
    public async Task Same_version_is_announced_once()
    {
        // Arrange
        await ReadyAsync();

        // Act
        await ReadyAsync();

        // Assert
        Activity.Feed.Should().ContainSingle();
    }

    [Fact]
    public async Task Failed_check_is_failed_with_the_reason()
    {
        // Arrange
        _source.CheckFailure = new HttpRequestException("No route to host");
        var updater = Updater();

        // Act
        var status = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (status, updater.Status, updater.Error).Should().Be((AppUpdateStatus.Failed, AppUpdateStatus.Failed, "No route to host"));
    }

    [Fact]
    public async Task Check_after_a_failure_tries_again()
    {
        // Arrange
        _source.CheckFailure = new HttpRequestException("offline");
        var updater = Updater();
        await updater.CheckAsync(TestContext.Current.CancellationToken);
        _source.CheckFailure = null;

        // Act
        var status = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (status, updater.Error, _source.Checks).Should().Be((AppUpdateStatus.UpToDate, (string?)null, 2));
    }

    [Fact]
    public async Task Failed_download_is_failed()
    {
        // Arrange
        _source.Offer = "0.5.0";
        _source.DownloadFailure = new IOException("Disk full");
        var updater = Updater();
        await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Act
        await updater.Download;

        // Assert
        (updater.Status, updater.Error, Activity.Feed.Count).Should().Be((AppUpdateStatus.Failed, "Disk full", 0));
    }

    [Fact]
    public async Task Check_while_checking_does_nothing()
    {
        // Arrange
        _source.CheckGate = new TaskCompletionSource();
        var updater = Updater();
        var first = updater.CheckAsync(TestContext.Current.CancellationToken);

        // Act
        var second = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (second, _source.Checks).Should().Be((AppUpdateStatus.Checking, 1));
        _source.CheckGate.SetResult();
        await first;
    }

    [Fact]
    public async Task Check_while_ready_does_nothing()
    {
        // Arrange
        var updater = await ReadyAsync();

        // Act
        var status = await updater.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (status, _source.Checks).Should().Be((AppUpdateStatus.Ready, 1));
    }

    [Fact]
    public void Schedule_checks_every_six_hours()
    {
        // Arrange
        var updater = Updater();

        // Act
        updater.StartSchedule();

        // Assert
        _clock.Timers.Should().ContainSingle().Which.Should().Be((AppUpdater.CheckInterval, AppUpdater.CheckInterval));
    }

    [Fact]
    public async Task Scheduled_tick_checks_again()
    {
        // Arrange
        var updater = Updater();
        await updater.CheckAsync(TestContext.Current.CancellationToken);
        updater.StartSchedule();

        // Act
        _clock.Tick();

        // Assert
        _source.Checks.Should().Be(2);
    }

    [Fact]
    public void Development_build_schedules_nothing()
    {
        // Arrange
        _source.Install = AppInstallKind.Development;
        var updater = Updater();

        // Act
        updater.StartSchedule();

        // Assert
        _clock.Timers.Should().BeEmpty();
    }

    [Fact]
    public async Task Restart_hands_the_update_over_and_quits()
    {
        // Arrange
        var updater = await ReadyAsync();

        // Act
        var restarted = updater.Restart();

        // Assert
        (restarted, _source.Applied?.Version, _quit.Quits, updater.Status).Should().Be((true, "0.5.0", 1, AppUpdateStatus.Restarting));
    }

    [Fact]
    public void Restart_without_a_ready_update_does_nothing()
    {
        // Arrange
        var updater = Updater();

        // Act
        var restarted = updater.Restart();

        // Assert
        (restarted, _source.Applied, _quit.Quits).Should().Be((false, (AppUpdateOffer?)null, 0));
    }

    [Fact]
    public async Task Restart_waits_for_an_install()
    {
        // Arrange
        var updater = await ReadyAsync();
        Activity.SetTask(new ActivityTask("install", ActivityTaskKind.Install, null, "Alpha", "Installing", null, null, Now));

        // Act
        var restarted = updater.Restart();

        // Assert
        (restarted, updater.RestartBlock, _quit.Quits, updater.Status).Should().Be((false, AppRestartBlock.Installing, 0, AppUpdateStatus.Ready));
    }

    [Fact]
    public void Generation_running_asks_before_a_restart()
    {
        // Arrange
        var updater = Updater();

        // Act
        Activity.SetTask(new ActivityTask("gen", ActivityTaskKind.Generation, null, "Alpha", "Generating", null, null, Now));

        // Assert
        updater.RestartBlock.Should().Be(AppRestartBlock.Working);
    }

    [Fact]
    public async Task Updater_that_fails_to_start_leaves_the_app_running()
    {
        // Arrange
        var updater = await ReadyAsync();
        _source.ApplyFailure = new InvalidOperationException("Update.exe is missing");

        // Act
        var restarted = updater.Restart();

        // Assert
        (restarted, _quit.Quits, updater.Status, updater.Error).Should().Be((false, 0, AppUpdateStatus.Failed, "Update.exe is missing"));
    }

    /// <summary>A fixed clock that records the timers created and fires them on <see cref="Tick"/>.</summary>
    private sealed class TimerClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _callbacks = [];

        public List<(TimeSpan DueTime, TimeSpan Period)> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers.Add((dueTime, period));
            _callbacks.Add((callback, state));
            return new NoTimer();
        }

        public void Tick()
        {
            foreach (var (callback, state) in _callbacks)
            {
                callback(state);
            }
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
