using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;
using static AiLocalPack.Pipeline.Tests.FakeAgentProgram;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>A job through Setup, Extract and Glossary with the fake agent, as the runner saves it (task 49).</summary>
public sealed class PipelineJobTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GenerationJobs _jobs;
    private readonly Game _game;
    private readonly WorkingCopies _copies;

    public PipelineJobTests()
    {
        _dir.RpgMakerGame("games/sample");
        _game = StageHarness.Game with { InstallPath = _dir.Combine("games/sample") };
        var paths = AppPaths.UnderHome(_dir.Combine("home"));
        _copies = new WorkingCopies(paths, FileCloner.Create(), new SystemDiskSpace());
        _jobs = new GenerationJobs(new GenerationJobStore(paths), new GenerationRunner());
    }

    public void Dispose() => _dir.Dispose();

    private async Task<GenerationJob> RunAsync(string recording, bool review = false)
    {
        var services = new PipelineServices(
            _game,
            new PipelineAgent(Adapter(recording), StageHarness.Server),
            RecipeTools.Builtin,
            new GenerationEstimator(RecipeTools.Builtin, _copies),
            _copies);
        var run = _jobs.Start(
            _game,
            AntiCheatPolicy.Bundled,
            null,
            "ru",
            "fake",
            new GenerationOptions(review),
            _ => GenerationPipeline.Create(services, pauseBefore: GenerationStage.Translate),
            cancellationToken: TestContext.Current.CancellationToken);
        return await run.Completion;
    }

    [Fact]
    public async Task The_job_reaches_translate_with_strings_and_glossary()
    {
        // Arrange
        var recording = Write(_dir, "ok.jsonl", SaveGlossary(RussianGlossary), Done());

        // Act
        var job = await RunAsync(recording);

        // Assert
        new { job.Status, job.Stage, job.Counters }.Should().BeEquivalentTo(new
        {
            Status = GenerationJobStatus.Paused,
            Stage = GenerationStage.Translate,
            Counters = new GenerationCounters(64, 0, 2, 0),
        });
    }

    [Fact]
    public async Task With_review_the_job_waits_for_the_player_after_the_glossary()
    {
        // Arrange
        var recording = Write(_dir, "ok.jsonl", SaveGlossary(RussianGlossary), Done());

        // Act
        var job = await RunAsync(recording, review: true);

        // Assert
        job.Status.Should().Be(GenerationJobStatus.WaitingForReview);
    }

    [Fact]
    public async Task An_agent_error_leaves_the_job_failed_at_glossary_with_the_reason()
    {
        // Arrange
        var recording = Write(_dir, "fail.jsonl", Event(new Agents.AgentDone(Agents.AgentOutcome.Failed, Message: "Overloaded.")));

        // Act
        var job = await RunAsync(recording);

        // Assert
        new { job.Status, job.Failure }.Should().BeEquivalentTo(new
        {
            Status = GenerationJobStatus.Failed,
            Failure = new GenerationFailure(GenerationStage.Glossary, GenerationFailureKind.Agent, "Overloaded."),
        });
    }

    [Fact]
    public async Task The_limit_mid_stage_pauses_the_job_until_the_reset()
    {
        // Arrange
        var reset = DateTimeOffset.UtcNow.AddHours(3);
        var recording = Write(
            _dir,
            "limit.jsonl",
            Event(new Agents.AgentLimitReached(reset, "limit")),
            Event(new Agents.AgentDone(Agents.AgentOutcome.LimitReached, Message: "limit")));

        // Act
        var job = await RunAsync(recording);

        // Assert
        new { job.Status, job.Stage, job.PauseReason, ResumeAt = job.ResumeAt?.ToUnixTimeSeconds() }.Should().BeEquivalentTo(new
        {
            Status = GenerationJobStatus.Paused,
            Stage = GenerationStage.Glossary,
            PauseReason = (GenerationPauseReason?)GenerationPauseReason.Limit,
            ResumeAt = (long?)reset.ToUnixTimeSeconds(),
        });
    }
}
