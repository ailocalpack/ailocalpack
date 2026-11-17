using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Setup (_Live game folder_ in <c>docs/generation.md</c>): no copy of the game is made. It checks the game folder
/// is there and the disk has room for the job's own files (the rebuilt files + a margin), then records the folder in
/// the job (<see cref="GenerationFiles.WriteGameFolder"/>), which every later stage reads and test-runs; a full working
/// copy an older job left is deleted. No agent runs; safe to repeat.
/// </summary>
public sealed class SetupStage(Game game, GenerationEstimator estimator) : IGenerationStage
{
    public GenerationStage Stage => GenerationStage.Setup;

    public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var estimate = estimator.Estimate(game.InstallPath, EngineKind.TryParse(game.Engine, out var kind) ? kind : null, cancellationToken, reader: context.Job.Options.Reader);
            if (!estimate.Space.IsEnough)
            {
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(
                    GenerationFailureKind.Disk,
                    $"The job's files need {estimate.Space.NeededBytes:N0} bytes; {estimate.Space.FreeBytes:N0} are free."));
            }

            GenerationJobFolders.DeleteLegacyCopy(context.Folder);
            context.Files.WriteGameFolder(game.InstallPath);
            context.Log($"Game folder: {Path.GetFullPath(game.InstallPath)} (read in place, no copy).");
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());
        }
        catch (DirectoryNotFoundException e)
        {
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(GenerationFailureKind.GameFolder, e.Message));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"The job folder can't be written: {e.Message}"));
        }
    }
}
