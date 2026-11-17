using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline;

/// <summary>What the stages of one job work with.</summary>
/// <param name="Game">The game (its folder is read in place and test-run; _Live game folder_).</param>
/// <param name="Agent">The job's agent with the toolkit server it starts.</param>
/// <param name="Tools">The machine's recipe tools (<see cref="RecipeTools.Create"/>).</param>
/// <param name="Estimator">Checks the disk before the job.</param>
/// <param name="Budget">The Translate stage's chunk size (<see cref="ChunkBudget.Default"/> when <c>null</c>).</param>
/// <param name="Build">What the Build stage needs; <c>null</c> = no Build handler (tests that stop before it).</param>
/// <param name="Translate">The Translate stage's retries and chunks at once (<see cref="TranslateOptions.Default"/> when <c>null</c>).</param>
/// <param name="Views">How the game is seen without its installed pack (Extract reads the originals); <c>null</c> = the folder as it is.</param>
public sealed record PipelineServices(
    Game Game,
    PipelineAgent Agent,
    RecipeTools Tools,
    GenerationEstimator Estimator,
    ChunkBudget? Budget = null,
    BuildServices? Build = null,
    TranslateOptions? Translate = null,
    GameViews? Views = null);

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
                new SetupStage(services.Game, services.Estimator),
                new ExtractStage(services.Tools, services.Budget, services.Agent, services.Game.Name, services.Views ?? services.Build?.Views),
                new StudyStage(services.Agent, services.Game.Name),
                new GlossaryStage(services.Agent, services.Game.Name),
                new TranslateStage(services.Agent, services.Game.Name, services.Tools, services.Translate),
                .. services.Build is { } build ? [new BuildStage(services.Game, services.Agent, services.Tools, build)] : Array.Empty<IGenerationStage>(),
                .. later ?? [],
            ],
            pauseBefore);
    }
}
