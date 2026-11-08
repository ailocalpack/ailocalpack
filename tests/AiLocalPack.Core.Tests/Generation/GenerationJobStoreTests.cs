using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary><c>job.json</c> persistence, one job per game, crash recovery (task 47).</summary>
public sealed class GenerationJobStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new(T0);
    private readonly AppPaths _paths;
    private readonly GenerationJobStore _store;

    public GenerationJobStoreTests()
    {
        _paths = AppPaths.UnderHome(_dir.Combine("home"));
        _store = new GenerationJobStore(_paths, _clock);
    }

    public void Dispose() => _dir.Dispose();

    private Game Game(uint appId = 620) =>
        new(GameId.Steam(appId), "Sample Quest", GameSource.Steam, _dir.Combine("game"), null, null, "rpgmaker:mz", "none", null, true);

    private static GenerationJob FullJob() => new()
    {
        Id = "20261108-120000-abcdef",
        Game = GameId.Steam(620),
        GameName = "Sample Quest «Ёж»",
        Language = "ru",
        Agent = "claude",
        Options = new GenerationOptions(true, WorkingCopyScope.AllFiles, "opus"),
        Stage = GenerationStage.Translate,
        Status = GenerationJobStatus.Paused,
        PauseReason = GenerationPauseReason.Limit,
        ResumeAt = T0.AddHours(3),
        Counters = new GenerationCounters(120, 50, 3, 1),
        Created = T0,
        Updated = T0.AddMinutes(30),
        ActiveTime = TimeSpan.FromMinutes(25),
        ChunkTime = TimeSpan.FromMinutes(4),
        EstimatedTime = TimeSpan.FromHours(1),
    };

    [Fact]
    public void A_job_reads_back_as_written()
    {
        // Arrange
        var job = FullJob();

        // Act
        var (read, error) = GenerationJobSerializer.Read(GenerationJobSerializer.Write(job));

        // Assert
        new { read, error }.Should().BeEquivalentTo(new { read = job, error = (string?)null });
    }

    [Fact]
    public void A_failure_reads_back_as_written()
    {
        // Arrange
        var job = FullJob() with
        {
            Status = GenerationJobStatus.Failed,
            PauseReason = null,
            ResumeAt = null,
            Failure = new GenerationFailure(GenerationStage.Glossary, GenerationFailureKind.SignedOut, "Not logged in"),
        };

        // Act
        var (read, _) = GenerationJobSerializer.Read(GenerationJobSerializer.Write(job));

        // Assert
        read.Should().BeEquivalentTo(job);
    }

    [Fact]
    public void Enums_are_written_camelCase()
    {
        // Arrange
        var job = FullJob();

        // Act
        var text = Encoding.UTF8.GetString(GenerationJobSerializer.Write(job));

        // Assert
        text.Should().Contain("\"status\": \"paused\"").And.Contain("\"stage\": \"translate\"").And.Contain("\"pauseReason\": \"limit\"");
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{\"schemaVersion\":2}", "newer than this app reads")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"j\",\"game\":\"steam:1\",\"language\":\"ru\",\"agent\":\"fake\",\"stage\":\"later\",\"status\":\"running\",\"created\":\"2026-01-01T00:00:00Z\",\"updated\":\"2026-01-01T00:00:00Z\"}", "unknown")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"j\",\"game\":\"nope\",\"language\":\"ru\",\"agent\":\"fake\",\"stage\":\"setup\",\"status\":\"running\",\"created\":\"2026-01-01T00:00:00Z\",\"updated\":\"2026-01-01T00:00:00Z\"}", "not a game ID")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"j\",\"game\":\"steam:1\",\"language\":\"ru\",\"agent\":\"fake\",\"stage\":\"setup\",\"status\":\"running\",\"created\":\"2026-01-01T00:00:00Z\",\"updated\":\"2026-01-01T00:00:00Z\",\"counters\":{\"chunks\":1,\"chunksDone\":2}}", "out of range")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"j\",\"game\":\"steam:1\",\"language\":\"ru\",\"agent\":\"fake\",\"stage\":\"setup\",\"status\":\"running\"}", "required")]
    public void A_bad_file_is_refused_with_the_reason(string json, string reason)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(json);

        // Act
        var (job, error) = GenerationJobSerializer.Read(data);

        // Assert
        new { job, HasReason = error?.Contains(reason, StringComparison.Ordinal) }.Should().BeEquivalentTo(new { job = (GenerationJob?)null, HasReason = true });
    }

    [Fact]
    public void Create_writes_a_running_job_in_the_games_work_folder()
    {
        // Arrange
        var game = Game();

        // Act
        var (entry, jobLock) = _store.Create(game, "ru", "fake", GenerationOptions.Default);
        jobLock.Dispose();

        // Assert
        new
        {
            Parent = Path.GetDirectoryName(entry.Folder),
            GenerationJobStore.Read(entry.Folder).Status,
            GenerationJobStore.Read(entry.Folder).Stage,
        }.Should().Be(new { Parent = (string?)Path.Combine(_paths.WorkingCopies, "steam-620"), Status = GenerationJobStatus.Running, Stage = GenerationStage.Setup });
    }

    [Fact]
    public void A_second_open_job_for_the_same_game_is_refused()
    {
        // Arrange
        var (_, first) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        first.Dispose();

        // Act
        var act = () => _store.Create(Game(), "de", "fake", GenerationOptions.Default);

        // Assert
        act.Should().Throw<GenerationJobException>().Which.Problem.Should().Be(GenerationJobProblem.AlreadyExists);
    }

    [Fact]
    public void Another_game_gets_its_own_job()
    {
        // Arrange
        var (_, first) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        first.Dispose();

        // Act
        var (_, second) = _store.Create(Game(700), "ru", "fake", GenerationOptions.Default);
        second.Dispose();

        // Assert
        _store.List().Should().HaveCount(2);
    }

    [Fact]
    public void A_completed_job_doesnt_stop_a_new_one()
    {
        // Arrange
        var (entry, jobLock) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        jobLock.Dispose();
        GenerationJobStore.Write(entry.Folder, entry.Job with { Stage = GenerationStage.Done, Status = GenerationJobStatus.Completed });
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        var (next, nextLock) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        nextLock.Dispose();

        // Assert
        _store.FindOpen(GameId.Steam(620))!.Folder.Should().Be(next.Folder);
    }

    [Fact]
    public void Recover_marks_a_running_job_without_a_runner_interrupted()
    {
        // Arrange
        var (entry, jobLock) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        jobLock.Dispose();

        // Act
        var recovered = _store.Recover();

        // Assert
        new { Count = recovered.Count, GenerationJobStore.Read(entry.Folder).Status }.Should().Be(new { Count = 1, Status = GenerationJobStatus.Interrupted });
    }

    [Fact]
    public void Recover_leaves_a_job_whose_runner_holds_the_lock()
    {
        // Arrange
        var (entry, jobLock) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);

        // Act
        var recovered = _store.Recover();
        jobLock.Dispose();

        // Assert
        new { Count = recovered.Count, GenerationJobStore.Read(entry.Folder).Status }.Should().Be(new { Count = 0, Status = GenerationJobStatus.Running });
    }

    [Fact]
    public void A_locked_job_cant_be_locked_twice()
    {
        // Arrange
        var (entry, jobLock) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        using var held = jobLock;

        // Act
        var second = GenerationJobLock.TryAcquire(entry.Folder);

        // Assert
        second.Should().BeNull();
    }

    [Fact]
    public void A_broken_job_file_is_left_out_of_the_list()
    {
        // Arrange
        var (_, jobLock) = _store.Create(Game(), "ru", "fake", GenerationOptions.Default);
        jobLock.Dispose();
        _dir.File("home/data/work/steam-700/broken/job.json", "{ nope");

        // Act
        var jobs = _store.List();

        // Assert
        jobs.Select(j => j.Job.Game).Should().Equal(GameId.Steam(620));
    }

    [Fact]
    public void Reading_a_missing_job_says_not_found()
    {
        // Arrange
        var folder = _dir.Combine("nowhere");

        // Act
        var act = () => GenerationJobStore.Read(folder);

        // Assert
        act.Should().Throw<GenerationJobException>().Which.Problem.Should().Be(GenerationJobProblem.NotFound);
    }
}
