using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Setup: the working copy of the game in the job folder (task 46), cloned where the file system can. A copy left
/// half made by a crash is made again (the step is safe to repeat). No agent runs.
/// </summary>
public sealed class SetupStage(Game game, GenerationEstimator estimator, WorkingCopies copies) : IGenerationStage
{
    public GenerationStage Stage => GenerationStage.Setup;

    public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var estimate = estimator.Estimate(game.InstallPath, EngineKind.TryParse(game.Engine, out var kind) ? kind : null, context.Job.Options.Scope, cancellationToken);
            copies.DeleteCopy(context.Folder);
            var copy = copies.Create(estimate.Copy, context.Folder, estimate.ReserveBytes, cancellationToken: cancellationToken);
            context.Log($"Working copy: {copy.Files} files, {copy.ClonedBytes} bytes cloned, {copy.CopiedBytes} copied.");
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());
        }
        catch (WorkingCopyException e)
        {
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(
                e.Problem == WorkingCopyProblem.DiskFull ? GenerationFailureKind.Disk : GenerationFailureKind.WorkingCopy, e.Message));
        }
    }
}
