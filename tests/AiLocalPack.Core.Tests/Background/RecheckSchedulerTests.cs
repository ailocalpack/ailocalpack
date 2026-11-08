using AiLocalPack.Core.Background;

namespace AiLocalPack.Core.Tests.Background;

/// <summary>When the tray app re-checks installed packs (task 39).</summary>
public sealed class RecheckSchedulerTests
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    private sealed class Runs
    {
        public int Count { get; private set; }

        public TaskCompletionSource? Gate { get; set; }

        public Exception? Throw { get; set; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Count++;
            if (Throw is { } error)
            {
                throw error;
            }

            if (Gate is { } gate)
            {
                await gate.Task;
            }
        }
    }

    private static (RecheckScheduler Scheduler, FakeClock Clock, Runs Runs) Create()
    {
        var clock = new FakeClock();
        var runs = new Runs();
        return (new RecheckScheduler(clock, runs.RunAsync, Period, Quiet), clock, runs);
    }

    [Fact]
    public async Task Nothing_runs_right_after_the_start_up_checks()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        clock.Advance(TimeSpan.FromHours(1));

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((false, 0));
    }

    [Fact]
    public async Task Runs_once_the_period_is_over()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        clock.Advance(Period);

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((true, 1));
    }

    [Fact]
    public async Task Period_counts_from_the_last_run()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        clock.Advance(Period);
        await scheduler.TickAsync(TestContext.Current.CancellationToken);
        clock.Advance(Period - TimeSpan.FromMinutes(1));

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((false, 1));
    }

    [Fact]
    public async Task Manifest_change_runs_after_the_quiet_time()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        scheduler.Notify();
        clock.Advance(Quiet);

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((true, 1));
    }

    [Fact]
    public async Task Changes_still_coming_keep_the_run_waiting()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        scheduler.Notify();
        clock.Advance(TimeSpan.FromMinutes(1));
        scheduler.Notify();
        clock.Advance(TimeSpan.FromMinutes(1.5));

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((false, 0));
    }

    [Fact]
    public async Task Change_handled_by_a_run_does_not_run_again()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        scheduler.Notify();
        clock.Advance(Quiet);
        await scheduler.TickAsync(TestContext.Current.CancellationToken);
        clock.Advance(Quiet);

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((false, 1));
    }

    [Fact]
    public async Task Tick_during_a_run_does_not_start_another()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        runs.Gate = new TaskCompletionSource();
        clock.Advance(Period);
        var first = scheduler.TickAsync(TestContext.Current.CancellationToken);
        clock.Advance(Period);

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        runs.Gate.SetResult();
        await first;
        (ran, runs.Count).Should().Be((false, 1));
    }

    [Fact]
    public async Task Change_during_a_run_makes_one_more_run_after_it()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        runs.Gate = new TaskCompletionSource();
        clock.Advance(Period);
        var first = scheduler.TickAsync(TestContext.Current.CancellationToken);
        scheduler.Notify();
        runs.Gate.SetResult();
        await first;
        clock.Advance(Quiet);

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((true, 2));
    }

    [Fact]
    public async Task Failing_run_is_swallowed_and_the_next_period_tries_again()
    {
        // Arrange
        var (scheduler, clock, runs) = Create();
        runs.Throw = new IOException("disk");
        clock.Advance(Period);
        await scheduler.TickAsync(TestContext.Current.CancellationToken);
        runs.Throw = null;
        clock.Advance(Period);

        // Act
        var ran = await scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        (ran, runs.Count).Should().Be((true, 2));
    }
}
