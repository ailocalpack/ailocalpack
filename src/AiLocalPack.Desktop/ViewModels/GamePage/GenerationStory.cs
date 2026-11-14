using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>One item of the job's story at the top of the Create with AI conversation.</summary>
public abstract record GenerationStoryItem;

/// <summary>The player's start request as their first message ("Create a Balanced translation into Русский with Claude Code.").</summary>
public sealed record GenerationStoryRequest(string Text) : GenerationStoryItem;

/// <summary>A finished stage as a line between rules ("Extract · strings: 1,842").</summary>
public sealed record GenerationStoryLine(string Text) : GenerationStoryItem;

/// <summary>
/// The job told as a conversation (decided 2026-11-14, design §6 "one conversation"): the start request, then a line per
/// finished stage. The running stage is not a line: the progress card, the glossary review or the state banner follows
/// the story in its place.
/// </summary>
public static class GenerationStory
{
    /// <summary>The story of <paramref name="job"/>; <paramref name="pack"/> (the result) adds the Build &amp; test line.</summary>
    public static IReadOnlyList<GenerationStoryItem> Of(GenerationJob? job, LocalPack? pack, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (job is null)
        {
            return [];
        }

        var items = new List<GenerationStoryItem> { new GenerationStoryRequest(Request(job, localizer)) };
        var done = job.Stage == GenerationStage.Done || pack is not null;
        foreach (var stage in GenerationSteps.For(job.Options.Quality))
        {
            if (!done && stage >= job.Stage)
            {
                break;
            }

            if (Line(stage, job, pack, localizer) is { } line)
            {
                items.Add(new GenerationStoryLine(line));
            }
        }

        return items;
    }

    private static string Request(GenerationJob job, Localizer localizer)
    {
        var agent = AgentInfo.Of(job.Agent).Name;
        var language = LanguageNames.Native(job.Language);
        var text = job.Options.Base is not null
            ? localizer.Format(nameof(Strings.Gen_Story_RequestUpdate), language, agent)
            : localizer.Format(nameof(Strings.Gen_Story_Request), GenerationQualityTexts.Name(job.Options.Quality, localizer), language, agent);
        return job.Options.ReviewGlossary ? text + " " + localizer[nameof(Strings.Gen_Story_RequestReview)] : text;
    }

    private static string? Line(GenerationStage stage, GenerationJob job, LocalPack? pack, Localizer localizer) => stage switch
    {
        GenerationStage.Setup => localizer[nameof(Strings.Gen_Story_Setup)],
        GenerationStage.Extract => job.Counters.Strings > 0
            ? localizer.Format(nameof(Strings.Gen_Story_Extract), job.Counters.Strings.ToString("N0", localizer.Culture))
            : localizer[nameof(Strings.Gen_Story_ExtractDone)],
        GenerationStage.Study => localizer[nameof(Strings.Gen_Story_Study)],
        GenerationStage.Glossary => localizer[job.Options.ReviewGlossary ? nameof(Strings.Gen_Story_GlossaryReviewed) : nameof(Strings.Gen_Story_Glossary)],
        GenerationStage.Translate => job.Counters.Chunks > 0
            ? localizer.Format(nameof(Strings.Gen_Story_Translate), job.Counters.Chunks.ToString("N0", localizer.Culture))
            : localizer[nameof(Strings.Gen_Story_TranslateDone)],
        GenerationStage.Build when pack is not null => localizer[pack.Report.TestRun.Outcome == TestRunOutcome.Passed
            ? nameof(Strings.Gen_Story_BuildPassed)
            : nameof(Strings.Gen_Story_Build)],
        _ => null,
    };
}
