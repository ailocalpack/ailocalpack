using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Glossary (task 49): the agent reads the extracted strings through the toolkit server and stores the names,
/// terms and tone with <c>put_glossary</c> (<see cref="PromptTemplate.Glossary"/>). The stage is done once
/// <c>glossary.json</c> holds at least one entry or a tone; an agent that ends without saving one fails it. A run again
/// (resume, retry) starts from the glossary stored so far: the prompt tells the agent to complete it. With the job's
/// review option the job then waits for the player (<see cref="GenerationJobMachine"/>; the review screen is task 54).
/// </summary>
public sealed class GlossaryStage(PipelineAgent agent, string gameName) : IGenerationStage
{
    public GenerationStage Stage => GenerationStage.Glossary;

    public async Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SourceStrings? source;
        try
        {
            source = SourceStringsFile.Read(context.Files);
        }
        catch (GenerationFileException e)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, $"{e.Message}; run the Extract stage again.");
        }

        if (source is null)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, "source.json is missing: the Extract stage didn't store the strings.");
        }

        var values = PromptValues.Glossary(gameName, context.Job.Language, source.Engine, source.Strings.Count, source.Files.Count);
        var run = await agent.RunAsync(context, PromptTemplate.Glossary, values, gameName, cancellationToken).ConfigureAwait(false);
        if (run.Stop is { } stop)
        {
            return stop;
        }

        Glossary? glossary;
        try
        {
            glossary = context.Files.ReadGlossary();
        }
        catch (GenerationFileException e)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Agent, $"The glossary the agent saved can't be read: {e.Message}");
        }

        if (glossary is null || (glossary.Entries.Count == 0 && glossary.Tone is null))
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Agent, $"{agent.DisplayName} finished without saving a glossary.");
        }

        if (glossary.Language != context.Job.Language)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Agent, $"The saved glossary is in '{glossary.Language}', not '{context.Job.Language}'.");
        }

        context.Log($"Glossary: {glossary.Entries.Count} terms" + (glossary.Tone is { } tone ? $", tone: {tone}" : ", no tone") + ".");
        return new GenerationStepResult.StageDone();
    }
}
