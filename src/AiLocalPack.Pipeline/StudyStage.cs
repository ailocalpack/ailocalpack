using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Study (decided 2026-11-13; <see cref="GenerationQuality.Thorough"/> jobs only, the others skip it): one run of
/// the strongest model reads every string in order (and, with <see cref="GenerationOptions.WebLookup"/>, the web: its
/// search and fetch tools only in this run) and replies with the game brief (<see cref="PromptTemplate.Study"/>), which is
/// stored in the job folder's <c>brief.md</c> and appended to the system prompt of the Glossary and Translate runs
/// (<see cref="PromptTemplate.SystemBrief"/>). The reply is cut to the brief (from its first <c>## </c> heading) and to
/// <see cref="MaxLength"/> characters at a line end, since every chunk carries it; a reply without a heading or shorter
/// than <see cref="MinLength"/> fails the stage. A run again (resume, retry) writes the brief again.
/// </summary>
public sealed class StudyStage(PipelineAgent agent, string gameName) : IGenerationStage
{
    /// <summary>A brief shorter than this says nothing useful.</summary>
    public const int MinLength = 200;

    /// <summary>About 6 000 tokens: what every chunk's prompt may carry.</summary>
    public const int MaxLength = 24_000;

    public GenerationStage Stage => GenerationStage.Study;

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

        var web = context.Job.Options.WebLookup;
        var values = PromptValues.Study(gameName, context.Job.Language, source.Engine, source.Strings.Count, source.Files.Count, web);
        var run = await agent.RunAsync(
            context,
            PromptTemplate.Study,
            values,
            gameName,
            cancellationToken,
            extraTools: web ? new HashSet<AgentBuiltinTool> { AgentBuiltinTool.WebSearch } : null).ConfigureAwait(false);
        if (run.Stop is { } stop)
        {
            return stop;
        }

        if (Brief(run.Reply) is not { } brief)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Agent, $"{agent.DisplayName} finished without writing the game brief.");
        }

        try
        {
            context.Files.WriteBrief(brief);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"brief.md can't be written: {e.Message}");
        }

        context.Log($"Study: game brief of {brief.Length:N0} characters" + (web ? ", web lookup on." : "."));
        return new GenerationStepResult.StageDone();
    }

    /// <summary>The brief in the agent's reply: from its first <c>## </c> heading, cut to <see cref="MaxLength"/> at a line end; <c>null</c> when there is none.</summary>
    internal static string? Brief(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var text = reply.ReplaceLineEndings("\n");
        var start = text.StartsWith("## ", StringComparison.Ordinal) ? 0 : text.IndexOf("\n## ", StringComparison.Ordinal) + 1;
        if (start < 0 || (start == 0 && !text.StartsWith("## ", StringComparison.Ordinal)))
        {
            return null;
        }

        text = text[start..].Trim();
        if (text.Length > MaxLength)
        {
            var cut = text.LastIndexOf('\n', MaxLength);
            text = text[..(cut > 0 ? cut : MaxLength)].TrimEnd();
        }

        return text.Length < MinLength ? null : text;
    }
}
