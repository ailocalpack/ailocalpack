using System.Globalization;
using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Tests.Generation;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Activity;

/// <summary>Generation jobs in the Activity panel (task 53): the running task with ⏸, the limit pause as a decision.</summary>
public sealed class GenerationActivityRecorderTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Build(FakeClock clock)
    {
        var services = TestServices.Build(s => s.AddSingleton<TimeProvider>(clock), steam => steam.AddGame(10, "Alpha"));
        services.GetRequiredService<GenerationActivityRecorder>();
        return services;
    }

    private static Game Alpha(ServiceProvider services) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));

    private static Task StartAsync(ServiceProvider services) =>
        services.GetRequiredService<GenerationService>().StartAsync(Alpha(services), "ru", "claude", GenerationOptions.Default, null);

    /// <summary>The fake stages stop Setup with the agent's limit, resetting in an hour from the clock's now.</summary>
    private static void LimitAtSetup(ServiceProvider services, FakeClock clock)
    {
        var stages = services.GetRequiredService<FakeGenerationStages>();
        stages.Hold = false;
        stages.Results = stage => stage == GenerationStage.Setup ? new GenerationStepResult.LimitReached(clock.GetUtcNow().AddHours(1)) : null;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never came true.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Running_job_is_a_running_task_that_can_be_paused_not_cancelled()
    {
        // Arrange
        using var services = Build(new FakeClock(Noon));

        // Act
        await StartAsync(services);

        // Assert
        var task = services.GetRequiredService<ActivityStore>().Tasks.Single();
        (task.Kind, task.Title, task.Step, task.Pause is not null, task.Cancel is null)
            .Should().Be((ActivityTaskKind.Generation, "Generating · Русский", "Making the working copy", true, true));
    }

    [Fact]
    public async Task Pausing_from_the_task_stops_the_job_and_ends_the_task()
    {
        // Arrange
        using var services = Build(new FakeClock(Noon));
        await StartAsync(services);
        var store = services.GetRequiredService<ActivityStore>();

        // Act
        store.Tasks.Single().Pause!();

        // Assert
        await WaitUntil(() => store.Tasks.Count == 0);
        services.GetRequiredService<GenerationService>().FindOpen(GameId.Steam(10))!.Status.Should().Be(GenerationJobStatus.Paused);
    }

    [Fact]
    public async Task Limit_pause_is_a_decision_saying_when_it_resumes()
    {
        // Arrange
        var clock = new FakeClock(Noon);
        using var services = Build(clock);
        LimitAtSetup(services, clock);
        var store = services.GetRequiredService<ActivityStore>();

        // Act
        await StartAsync(services);

        // Assert
        await WaitUntil(() => store.Decisions.Count == 1);
        var decision = store.Decisions.Single();
        (decision.Kind, decision.Until, decision.Tone).Should().Be((ActivityEventKind.GenerationPaused, Noon.AddHours(1), ActivityTone.Paused));
    }

    [Fact]
    public async Task Resumed_job_settles_its_limit_decision()
    {
        // Arrange
        var clock = new FakeClock(Noon);
        using var services = Build(clock);
        LimitAtSetup(services, clock);
        var store = services.GetRequiredService<ActivityStore>();
        await StartAsync(services);
        var jobs = services.GetRequiredService<GenerationService>();
        await WaitUntil(() => store.Decisions.Count == 1 && !jobs.IsRunning(GameId.Steam(10)));
        var stages = services.GetRequiredService<FakeGenerationStages>();
        stages.Results = null;
        stages.Hold = true;

        // Act
        await jobs.ResumeAsync(Alpha(services));

        // Assert
        (store.Decisions.Count, store.Feed.Single().Kind).Should().Be((0, ActivityEventKind.GenerationPaused));
    }

    [Fact]
    public async Task Panel_offers_resume_now_on_the_limit_decision()
    {
        // Arrange
        var clock = new FakeClock(Noon);
        using var services = Build(clock);
        LimitAtSetup(services, clock);
        var store = services.GetRequiredService<ActivityStore>();
        await StartAsync(services);
        await WaitUntil(() => store.Decisions.Count == 1);

        // Act
        var row = services.GetRequiredService<ActivityPanelViewModel>().Decisions.Single();

        // Assert
        var resumes = new DateTime(2026, 11, 9, 13, 0, 0).ToString("t", CultureInfo.GetCultureInfo("en"));
        (row.Title, row.Detail, row.Actions.Single().Text, row.IsPaused)
            .Should().Be(("Generation paused: subscription limit", $"Resumes automatically at ~{resumes}", "Resume now", true));
    }
}
