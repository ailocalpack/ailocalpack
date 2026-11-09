using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline;

/// <summary>What the stages of one job work with.</summary>
/// <param name="Game">The game (its folder is only read, by Setup).</param>
/// <param name="Agent">The job's agent with the toolkit server it starts.</param>
/// <param name="Tools">The machine's recipe tools (<see cref="RecipeTools.Create"/>).</param>
/// <param name="Estimator">Plans the working copy.</param>
/// <param name="Copies">Makes it.</param>
/// <param name="Budget">The Translate stage's chunk size (<see cref="ChunkBudget.Default"/> when <c>null</c>).</param>
/// <param name="Build">What the Build stage needs; <c>null</c> = no Build handler (tests that stop before it).</param>
public sealed record PipelineServices(
    Game Game,
    PipelineAgent Agent,
    RecipeTools Tools,
    GenerationEstimator Estimator,
    WorkingCopies Copies,
    ChunkBudget? Budget = null,
    BuildServices? Build = null);

/// <summary>
/// The stage handlers of a generation job: Setup, Extract and Glossary (task 49), Translate (task 50), Build (task 51,
/// when <see cref="PipelineServices.Build"/> is given), then the stages given by the caller.
/// </summary>
public static class GenerationPipeline
{
    /// <param name="services">What the stages work with.</param>
    /// <param name="later">Handlers of the later stages.</param>
    /// <param name="pauseBefore">The stage the job pauses before (<c>gen run --until</c>), if any.</param>
    public static GenerationStages Create(PipelineServices services, IEnumerable<IGenerationStage>? later = null, GenerationStage? pauseBefore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new GenerationStages(
            [
                new SetupStage(services.Game, services.Estimator, services.Copies),
                new ExtractStage(services.Tools, services.Budget),
                new GlossaryStage(services.Agent, services.Game.Name),
                new TranslateStage(services.Agent, services.Game.Name, services.Tools),
                .. services.Build is { } build ? [new BuildStage(services.Game, services.Agent, services.Tools, build)] : Array.Empty<IGenerationStage>(),
                .. later ?? [],
            ],
            pauseBefore);
    }
}
