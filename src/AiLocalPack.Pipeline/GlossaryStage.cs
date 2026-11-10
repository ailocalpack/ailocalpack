using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Glossary (task 49): the agent reads the extracted strings through the toolkit server and stores the names,
/// terms and tone with <c>put_glossary</c> (<see cref="PromptTemplate.Glossary"/>). The stage is done once
/// <c>glossary.json</c> holds at least one entry or a tone; an agent that ends without saving one fails it. A run again
/// (resume, retry) starts from the glossary stored so far: the prompt tells the agent to complete it. With the job's
/// review option the job then waits for the player (<see cref="GenerationJobMachine"/>), who edits it and approves it or
/// has it drafted again (task 54: <see cref="GlossaryReview"/>; the entries the player edited stay, and an agent run that
/// stores only those still fails).
/// An update job (task 56) starts from its base pack instead: nothing to translate → done without the agent; the base
/// kept the glossary it was made with (a local pack) → that glossary, merged into what is stored, without the agent;
/// else the agent drafts one that follows the base's translations, which the string store already holds
/// (<see cref="PromptTemplate.GlossaryUpdate"/>).
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

        var update = context.Job.Options.Base;
        if (update is not null)
        {
            if (context.Job.Counters.Chunks == 0)
            {
                context.Log("Glossary: the update has no new or changed strings, so no glossary is needed.");
                return new GenerationStepResult.StageDone();
            }

            UpdateBase? updateBase;
            try
            {
                updateBase = context.Files.ReadBase();
                if (updateBase?.Glossary is { } kept && kept.Language == context.Job.Language)
                {
                    var merged = Merge(context.Files.ReadGlossary(), kept);
                    context.Files.WriteGlossary(merged);
                    context.Log($"Glossary: {merged.Entries.Count} terms from {update.PackId} rev {update.Revision}, no agent run.");
                    return new GenerationStepResult.StageDone();
                }
            }
            catch (GenerationFileException e)
            {
                return new GenerationStepResult.Failed(GenerationFailureKind.Disk, e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"glossary.json can't be written: {e.Message}");
            }
        }

        var template = update is null ? PromptTemplate.Glossary : PromptTemplate.GlossaryUpdate;
        var values = update is null
            ? PromptValues.Glossary(gameName, context.Job.Language, source.Engine, source.Strings.Count, source.Files.Count)
            : PromptValues.GlossaryUpdate(gameName, context.Job.Language, source.Engine, source.Strings.Count, source.Files.Count, update.Title, context.Job.Counters.Translated);
        var run = await agent.RunAsync(context, template, values, gameName, cancellationToken).ConfigureAwait(false);
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

        // Entries the player edited (kept by Regenerate) don't count: the agent must have stored something itself.
        if (glossary is null || !GlossaryReview.HasAgentPart(glossary))
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

    /// <summary>The stored glossary with the base's entries it lacks; the stored tone and form of address win.</summary>
    internal static Glossary Merge(Glossary? stored, Glossary kept)
    {
        if (stored is null || stored.Language != kept.Language)
        {
            return kept;
        }

        var terms = stored.Entries.Select(e => e.Term).ToHashSet(Glossary.TermComparer);
        return stored with
        {
            Tone = stored.Tone ?? kept.Tone,
            FormOfAddress = stored.FormOfAddress ?? kept.FormOfAddress,
            Entries = [.. stored.Entries, .. kept.Entries.Where(e => terms.Add(e.Term))],
        };
    }
}
