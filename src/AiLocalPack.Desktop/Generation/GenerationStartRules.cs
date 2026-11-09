using AiLocalPack.Core.Generation;

namespace AiLocalPack.Desktop.Generation;

/// <summary>Where the start form's size estimate is.</summary>
public enum EstimateState
{
    Estimating,
    Done,
    Failed,
}

/// <summary>Why <c>Start generation</c> is off; shown under the button.</summary>
public enum GenerationStartBlock
{
    /// <summary>The press is being handled (the job folder is being created).</summary>
    Starting,

    /// <summary>The game's folder is unreachable (drive unplugged, folder moved).</summary>
    GameUnavailable,

    /// <summary>The chosen agent isn't installed, signed in or recent enough yet.</summary>
    AgentNotReady,

    Estimating,

    EstimateFailed,

    /// <summary>
    /// The toolkit can't read the game (unknown engine, switched off on this OS, no tool, a reading error): the Extract
    /// stage would fail before any agent time, until the agent writes its own extractor (task 57).
    /// </summary>
    EngineUnsupported,

    /// <summary>The data drive hasn't room for the working copy.</summary>
    NotEnoughDisk,
}

/// <summary>What the start form knows when it decides whether a job can start.</summary>
/// <param name="EngineSupported">The toolkit read the strings (only meaningful once the estimate is done).</param>
/// <param name="EnoughDisk">The working copy fits (only meaningful once the estimate is done).</param>
public sealed record GenerationStartInputs(
    bool GameAvailable,
    bool AgentReady,
    EstimateState Estimate,
    bool EngineSupported,
    bool EnoughDisk,
    bool Starting = false);

/// <summary>
/// The start form's rule (design §6a): <c>Start generation</c> is on only when the agent is ready, the estimate is done,
/// the toolkit reads the game and there is room for the working copy; otherwise the first reason in this order is shown:
/// starting, game unavailable, agent, estimating, estimate failed, engine, disk.
/// </summary>
public static class GenerationStartRules
{
    /// <summary>Why the job can't start, or <c>null</c> when it can.</summary>
    public static GenerationStartBlock? Resolve(GenerationStartInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Starting)
        {
            return GenerationStartBlock.Starting;
        }

        if (!inputs.GameAvailable)
        {
            return GenerationStartBlock.GameUnavailable;
        }

        if (!inputs.AgentReady)
        {
            return GenerationStartBlock.AgentNotReady;
        }

        return inputs.Estimate switch
        {
            EstimateState.Estimating => GenerationStartBlock.Estimating,
            EstimateState.Failed => GenerationStartBlock.EstimateFailed,
            _ when !inputs.EngineSupported => GenerationStartBlock.EngineUnsupported,
            _ when !inputs.EnoughDisk => GenerationStartBlock.NotEnoughDisk,
            _ => null,
        };
    }
}

/// <summary>A step of the five-step header: Setup + the four pipeline stages.</summary>
public enum GenerationStepState
{
    /// <summary>Not reached yet: the stage's own icon.</summary>
    Upcoming,

    /// <summary>The start form: Setup waits for the player to start.</summary>
    Current,

    /// <summary>Running: a spinner.</summary>
    Running,

    Done,

    /// <summary>Waits for the player (glossary review): a pencil.</summary>
    Waiting,

    /// <summary>Paused by the limit or the player, or interrupted.</summary>
    Paused,

    Failed,
}

/// <summary>The header's steps for a job (or the start form when there is none).</summary>
public static class GenerationSteps
{
    /// <summary>Setup, Extract, Glossary, Translate, Build &amp; test.</summary>
    public static IReadOnlyList<GenerationStage> Stages { get; } =
        [GenerationStage.Setup, GenerationStage.Extract, GenerationStage.Glossary, GenerationStage.Translate, GenerationStage.Build];

    /// <summary>The state of each of <see cref="Stages"/>; <paramref name="job"/> <c>null</c> = the start form.</summary>
    public static IReadOnlyList<GenerationStepState> Of(GenerationJob? job)
    {
        if (job is null)
        {
            return [GenerationStepState.Current, .. Enumerable.Repeat(GenerationStepState.Upcoming, Stages.Count - 1)];
        }

        if (job.Stage == GenerationStage.Done || job.Status == GenerationJobStatus.Completed)
        {
            return [.. Enumerable.Repeat(GenerationStepState.Done, Stages.Count)];
        }

        var current = job.Status switch
        {
            GenerationJobStatus.Running => GenerationStepState.Running,
            GenerationJobStatus.WaitingForReview => GenerationStepState.Waiting,
            GenerationJobStatus.Failed => GenerationStepState.Failed,
            _ => GenerationStepState.Paused,
        };

        // A failure names its stage; the job's stage is the one that runs next otherwise.
        var stage = job.Status == GenerationJobStatus.Failed && job.Failure is { } failure ? failure.Stage : job.Stage;
        var index = Stages.ToList().IndexOf(stage);
        return [.. Stages.Select((_, i) => i < index ? GenerationStepState.Done : i == index ? current : GenerationStepState.Upcoming)];
    }
}
