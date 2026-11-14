using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Desktop.Generation;

/// <summary>Sizes up a generation for the start form (task 46's estimate), off the UI thread.</summary>
public interface IGenerationEstimates
{
    /// <param name="updateBase">An update's base string table (task 56): only the strings it lacks or that changed count.</param>
    /// <param name="reader">What reads the game (<see cref="GenerationOptions.Reader"/>); <c>null</c> = the detected engine's tool.</param>
    /// <exception cref="WorkingCopyException">The game folder is missing or can't be listed.</exception>
    /// <exception cref="OperationCanceledException">The form was closed.</exception>
    Task<GenerationSizeEstimate> EstimateAsync(Game game, StringTable? updateBase, string? reader, CancellationToken cancellationToken);
}

/// <summary>The real estimate: <see cref="GenerationEstimator"/> on a worker thread, with the engine the catalog stored.</summary>
public sealed class GenerationEstimates(GenerationEstimator estimator) : IGenerationEstimates
{
    public Task<GenerationSizeEstimate> EstimateAsync(Game game, StringTable? updateBase, string? reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        EngineKind? engine = EngineKind.TryParse(game.Engine, out var kind) ? kind : null;
        return Task.Run(() => estimator.Estimate(game.InstallPath, engine, cancellationToken: cancellationToken, updateBase: updateBase, reader: reader), cancellationToken);
    }
}
