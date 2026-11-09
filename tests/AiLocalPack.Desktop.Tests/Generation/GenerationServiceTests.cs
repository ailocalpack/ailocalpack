using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The app's generation jobs (task 52): start, follow, refuse, stop on quit.</summary>
public sealed class GenerationServiceTests
{
    private static ServiceProvider Build() =>
        TestServices.Build(steam: s => TestGames.AddShooter(s.AddGame(10, "Alpha")));

    private static Game GameOf(ServiceProvider services, uint appId) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(appId));

    [Fact]
    public async Task Started_job_is_the_game_s_open_job()
    {
        // Arrange
        using var services = Build();
        var jobs = services.GetRequiredService<GenerationService>();

        // Act
        var result = await jobs.StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, TimeSpan.FromHours(1));

        // Assert
        jobs.FindOpen(GameId.Steam(10))!.Id.Should().Be(result.Job!.Id);
    }

    [Fact]
    public async Task Start_tells_the_game_s_status()
    {
        // Arrange
        using var services = Build();
        var statuses = services.GetRequiredService<GameStatuses>();
        var told = new List<GameId>();
        statuses.JobChanged += (_, job) => told.Add(job.Game);

        // Act
        await services.GetRequiredService<GenerationService>().StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);

        // Assert
        (told.Contains(GameId.Steam(10)), statuses.Resolve(GameOf(services, 10)).Job is not null).Should().Be((true, true));
    }

    [Fact]
    public async Task Blocked_game_is_refused()
    {
        // Arrange
        using var services = Build();

        // Act
        var result = await services.GetRequiredService<GenerationService>().StartAsync(GameOf(services, 30), "ru", "claude", GenerationOptions.Default, null);

        // Assert
        (result.Started, result.Problem, services.GetRequiredService<FakeGenerationStages>().Jobs.Count).Should().Be((false, GenerationStartProblem.Blocked, 0));
    }

    [Fact]
    public async Task Second_job_for_the_game_is_refused()
    {
        // Arrange
        using var services = Build();
        var jobs = services.GetRequiredService<GenerationService>();
        await jobs.StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);

        // Act
        var result = await jobs.StartAsync(GameOf(services, 10), "de", "codex", GenerationOptions.Default, null);

        // Assert
        result.Problem.Should().Be(GenerationStartProblem.AlreadyExists);
    }

    [Fact]
    public async Task Stages_that_cant_be_set_up_are_reported()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeGenerationStages>().Refuse = new InvalidOperationException("The app's own path is unknown.");

        // Act
        var result = await services.GetRequiredService<GenerationService>().StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);

        // Assert
        (result.Problem, result.Detail).Should().Be((GenerationStartProblem.Unexpected, "The app's own path is unknown."));
    }

    [Fact]
    public async Task Finished_job_is_no_longer_open()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeGenerationStages>().Hold = false;
        var jobs = services.GetRequiredService<GenerationService>();
        var ended = new TaskCompletionSource();
        jobs.Changed += (_, job) =>
        {
            if (job.Status == GenerationJobStatus.Completed)
            {
                ended.TrySetResult();
            }
        };

        // Act
        await jobs.StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        jobs.FindOpen(GameId.Steam(10)).Should().BeNull();
    }

    [Fact]
    public async Task Quitting_leaves_a_running_job_interrupted()
    {
        // Arrange
        var services = Build();
        var store = services.GetRequiredService<GenerationJobStore>();
        var start = await services.GetRequiredService<GenerationService>().StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);
        var folder = store.List().Single(e => e.Job.Id == start.Job!.Id).Folder;

        // Act
        services.GetRequiredService<GenerationService>().Dispose();

        // Assert
        GenerationJobStore.Read(folder).Status.Should().Be(GenerationJobStatus.Interrupted);
        services.Dispose();
    }
}
