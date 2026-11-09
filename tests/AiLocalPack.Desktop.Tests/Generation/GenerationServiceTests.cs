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

    private static ServiceProvider Build(FakeClock clock) =>
        TestServices.Build(c => c.AddSingleton<TimeProvider>(clock), steam: s => TestGames.AddShooter(s.AddGame(10, "Alpha")));

    /// <summary>A job of Alpha saved on disk in the state <paramref name="change"/> gives it (no runner holds it).</summary>
    private static GenerationJobEntry JobOnDisk(ServiceProvider services, Func<GenerationJob, GenerationJob> change)
    {
        var (entry, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(GameOf(services, 10), "ru", "claude", GenerationOptions.Default);
        jobLock.Dispose();
        var job = change(entry.Job);
        GenerationJobStore.Write(entry.Folder, job);
        return entry with { Job = job };
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
    public async Task Resume_due_resumes_a_job_whose_limit_has_reset()
    {
        // Arrange
        var clock = new FakeClock();
        using var services = Build(clock);
        JobOnDisk(services, j => j with { Status = GenerationJobStatus.Paused, PauseReason = GenerationPauseReason.Limit, ResumeAt = clock.GetUtcNow().AddHours(1) });
        clock.Advance(TimeSpan.FromHours(2));

        // Act
        var resumed = await services.GetRequiredService<GenerationService>().ResumeDueAsync();

        // Assert
        (resumed.Count, services.GetRequiredService<GenerationService>().IsRunning(GameId.Steam(10))).Should().Be((1, true));
    }

    [Fact]
    public async Task Resume_due_leaves_a_job_whose_limit_hasnt_reset()
    {
        // Arrange
        var clock = new FakeClock();
        using var services = Build(clock);
        JobOnDisk(services, j => j with { Status = GenerationJobStatus.Paused, PauseReason = GenerationPauseReason.Limit, ResumeAt = clock.GetUtcNow().AddHours(1) });

        // Act
        var resumed = await services.GetRequiredService<GenerationService>().ResumeDueAsync();

        // Assert
        resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_due_leaves_a_job_the_player_paused()
    {
        // Arrange
        var clock = new FakeClock();
        using var services = Build(clock);
        JobOnDisk(services, j => j with { Status = GenerationJobStatus.Paused, PauseReason = GenerationPauseReason.Player });
        clock.Advance(TimeSpan.FromDays(1));

        // Act
        var resumed = await services.GetRequiredService<GenerationService>().ResumeDueAsync();

        // Assert
        resumed.Should().BeEmpty();
    }

    [Fact]
    public void Recover_marks_a_job_left_running_interrupted_and_tells()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => j);
        var jobs = services.GetRequiredService<GenerationService>();
        jobs.FindOpen(GameId.Steam(10));
        var told = new List<GenerationJobStatus>();
        jobs.Changed += (_, job) => told.Add(job.Status);

        // Act
        var (interrupted, open) = jobs.Recover();

        // Assert
        (interrupted.Count, open, jobs.FindOpen(GameId.Steam(10))!.Status, told.Single()).Should().Be((1, 1, GenerationJobStatus.Interrupted, GenerationJobStatus.Interrupted));
    }

    [Fact]
    public async Task Cancel_keeping_the_folder_cancels_a_stopped_job()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => j with { Status = GenerationJobStatus.Interrupted });
        var jobs = services.GetRequiredService<GenerationService>();

        // Act
        var result = await jobs.CancelAsync(GameId.Steam(10), deleteFolder: false);

        // Assert
        (result.Job?.Status, jobs.FindOpen(GameId.Steam(10)), Directory.Exists(entry.Folder)).Should().Be((GenerationJobStatus.Cancelled, (GenerationJob?)null, true));
    }

    [Fact]
    public async Task Cancel_with_delete_stops_a_running_job_and_deletes_its_folder()
    {
        // Arrange
        using var services = Build();
        var jobs = services.GetRequiredService<GenerationService>();
        await jobs.StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);
        var folder = jobs.FolderOf(GameId.Steam(10))!;

        // Act
        var result = await jobs.CancelAsync(GameId.Steam(10), deleteFolder: true);

        // Assert
        (result.Succeeded, result.Job?.Status, Directory.Exists(folder)).Should().Be((true, GenerationJobStatus.Cancelled, false));
    }

    [Fact]
    public async Task Cancel_without_a_job_is_not_found()
    {
        // Arrange
        using var services = Build();

        // Act
        var result = await services.GetRequiredService<GenerationService>().CancelAsync(GameId.Steam(10), deleteFolder: true);

        // Assert
        result.Problem.Should().Be(GenerationActionProblem.NotFound);
    }

    [Fact]
    public async Task Resume_of_a_blocked_game_is_refused()
    {
        // Arrange
        using var services = Build();
        var (entry, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(GameOf(services, 30), "ru", "claude", GenerationOptions.Default);
        jobLock.Dispose();
        GenerationJobStore.Write(entry.Folder, entry.Job with { Status = GenerationJobStatus.Interrupted });

        // Act
        var result = await services.GetRequiredService<GenerationService>().ResumeAsync(GameOf(services, 30));

        // Assert
        (result.Problem, services.GetRequiredService<FakeGenerationStages>().Jobs.Count).Should().Be((GenerationActionProblem.Blocked, 0));
    }

    [Fact]
    public void Details_read_progress_and_the_log_of_the_open_job()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => j with { Status = GenerationJobStatus.Interrupted });
        AiLocalPack.Pipeline.PipelineProgressFile.Update(new GenerationFiles(entry.Folder), p => p with { Recent = [new("k", "Hi", "Привет")] });
        GenerationJobLog.Append(entry.Folder, TimeProvider.System, "Chunk 1 of 2: 32 strings.");

        // Act
        var details = services.GetRequiredService<GenerationService>().ReadDetails(GameId.Steam(10));

        // Assert
        (details.Progress.Recent.Single().Translation, details.Log.Last().EndsWith("Chunk 1 of 2: 32 strings.", StringComparison.Ordinal)).Should().Be(("Привет", true));
    }

    [Fact]
    public async Task Pause_stops_a_running_job_as_paused_by_the_player()
    {
        // Arrange
        using var services = Build();
        var jobs = services.GetRequiredService<GenerationService>();
        await jobs.StartAsync(GameOf(services, 10), "ru", "claude", GenerationOptions.Default, null);

        // Act
        jobs.Pause(GameId.Steam(10));

        // Assert
        await WaitUntil(() => !jobs.IsRunning(GameId.Steam(10)));
        (jobs.FindOpen(GameId.Steam(10))!.Status, jobs.FindOpen(GameId.Steam(10))!.PauseReason).Should().Be((GenerationJobStatus.Paused, GenerationPauseReason.Player));
    }
}
