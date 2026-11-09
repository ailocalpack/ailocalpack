using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Pipeline;

/// <summary>How the Translate stage retries.</summary>
/// <param name="MaxAttempts">Agent runs per chunk at most: the first, then retries with only the strings still missing or refused.</param>
public sealed record TranslateOptions(int MaxAttempts = 3)
{
    public static TranslateOptions Default { get; } = new();
}

/// <summary>
/// Stage Translate (task 50), one step per chunk of <c>chunks.json</c> (<see cref="ChunkPlanner"/>, fixed by the Extract
/// stage; the step's chunk is <see cref="GenerationCounters.ChunksDone"/>, so a resumed job goes on with the chunk it
/// was at and never runs a finished one again). A step gives the agent the chunk's strings that have no stored
/// translation yet (originals, context, length limit, placeholders) with the glossary entries they mention and the
/// tone (<see cref="PromptTemplate.Translate"/>); the agent stores them with <c>put_translations</c>, which refuses
/// errors. Every stored text is checked again with the <see cref="StringValidator"/>; strings still missing or refused
/// go back to the agent (<see cref="PromptTemplate.TranslateRetry"/>, each with its problem) up to
/// <see cref="TranslateOptions.MaxAttempts"/> runs in all; warnings are kept. What is still missing after that stays
/// untranslated (logged) and the job goes on; a chunk where the agent stored nothing at all fails the stage. A limit
/// pauses the job at the chunk; translations stored before it are kept and not asked for again.
/// </summary>
public sealed class TranslateStage(PipelineAgent agent, string gameName, RecipeTools tools, TranslateOptions? options = null) : IGenerationStage
{
    private static readonly JsonWriterOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TranslateOptions _options = options ?? TranslateOptions.Default;

    public GenerationStage Stage => GenerationStage.Translate;

    public async Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var job = context.Job;
        var number = job.Counters.ChunksDone + 1;
        SourceStrings? source;
        IReadOnlyList<TranslationChunk>? plan;
        try
        {
            source = SourceStringsFile.Read(context.Files);
            plan = ChunkPlanFile.Read(context.Files);
        }
        catch (GenerationFileException e)
        {
            return Fail(context, GenerationFailureKind.Toolkit, $"{e.Message}; run the Extract stage again.");
        }

        if (source is null || plan is null)
        {
            return Fail(context, GenerationFailureKind.Toolkit, $"{(source is null ? "source.json" : "chunks.json")} is missing: the Extract stage didn't store it.");
        }

        if (plan.Count != job.Counters.Chunks || job.Counters.ChunksDone >= plan.Count)
        {
            return Fail(context, GenerationFailureKind.Toolkit, $"chunks.json has {plan.Count} chunks, the job counts {job.Counters.Chunks}; run the Extract stage again.");
        }

        var byKey = source.Strings.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var chunk = new List<ExtractedString>();
        foreach (var key in plan[job.Counters.ChunksDone].Keys)
        {
            if (!byKey.TryGetValue(key, out var s))
            {
                return Fail(context, GenerationFailureKind.Toolkit, $"chunks.json names '{key}', which source.json doesn't have; run the Extract stage again.");
            }

            chunk.Add(s);
        }

        var markup = tools.Find(source.Tool)?.Markup ?? TextMarkup.None;
        var glossary = Glossary(context);
        var label = $"Chunk {number} of {plan.Count}";
        var asked = 0;
        Dictionary<string, string?> problems;
        try
        {
            problems = Problems(context, chunk, markup, out _);
        }
        catch (GenerationFileException e)
        {
            return Fail(context, GenerationFailureKind.Disk, e.Message);
        }

        var first = problems.Count;
        var asking = problems.Keys.ToHashSet(StringComparer.Ordinal);
        if (first == 0)
        {
            context.Log($"{label}: all {chunk.Count} strings were already translated.");
        }

        for (var attempt = 1; attempt <= _options.MaxAttempts && problems.Count > 0; attempt++)
        {
            var pending = chunk.Where(s => problems.ContainsKey(s.Key)).ToList();
            var entries = GlossaryFilter.Relevant(glossary, pending.Select(s => s.Original));
            context.Log($"{label}: {pending.Count} strings, {entries.Count} glossary entries, attempt {attempt} of {_options.MaxAttempts}.");
            var retry = attempt > 1;
            var values = Values(gameName, job.Language, source, number, plan.Count, pending, entries, GlossaryReview.ToneForPrompt(glossary), retry ? problems : null);
            var run = await agent.RunAsync(context, retry ? PromptTemplate.TranslateRetry : PromptTemplate.Translate, values, gameName, cancellationToken).ConfigureAwait(false);
            asked++;
            if (run.Stop is { } stop)
            {
                return stop;
            }

            try
            {
                problems = Problems(context, chunk, markup, out _);
            }
            catch (GenerationFileException e)
            {
                return Fail(context, GenerationFailureKind.Disk, e.Message);
            }
        }

        StringTable stored;
        Dictionary<string, string> warnings;
        try
        {
            problems = Problems(context, chunk, markup, out warnings);
            stored = RemoveRefused(context, problems);
        }
        catch (GenerationFileException e)
        {
            return Fail(context, GenerationFailureKind.Disk, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(context, GenerationFailureKind.Disk, $"strings.json can't be written: {e.Message}");
        }

        if (first > 0 && problems.Count == first)
        {
            return Fail(context, GenerationFailureKind.Agent, $"{agent.DisplayName} stored no translations for chunk {number} after {asked} attempts.");
        }

        context.Log($"{label}: {chunk.Count - problems.Count} of {chunk.Count} strings translated"
            + (warnings.Count > 0 ? $", {warnings.Count} with warnings" : "")
            + (problems.Count > 0 ? $"; left untranslated after {asked} attempts: {string.Join(", ", problems.Keys.Take(20))}{(problems.Count > 20 ? $" and {problems.Count - 20} more" : "")}." : "."));
        var recent = chunk
            .Where(s => asking.Contains(s.Key) && stored.Strings.TryGetValue(s.Key, out var t) && !string.IsNullOrEmpty(t))
            .TakeLast(PipelineProgressFile.RecentCount)
            .Select(s => new RecentTranslation(s.Key, s.Original, stored.Strings[s.Key], warnings.GetValueOrDefault(s.Key)))
            .ToList();
        var chunkKeys = chunk.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var chunkWarnings = chunk
            .Where(s => warnings.ContainsKey(s.Key) && stored.Strings.ContainsKey(s.Key))
            .Select(s => new TranslationWarning(s.Key, warnings[s.Key], s.Original, stored.Strings[s.Key]))
            .ToList();
        PipelineProgressFile.Update(context.Files, p => p with
        {
            Recent = recent.Count > 0 ? recent : p.Recent,
            Warnings = [.. p.Warnings.Where(w => !chunkKeys.Contains(w.Key)), .. chunkWarnings],
        });

        var translated = source.Strings.Count(s => stored.Strings.TryGetValue(s.Key, out var t) && !string.IsNullOrEmpty(t));
        return new GenerationStepResult.ChunkDone(translated - job.Counters.Translated);
    }

    /// <summary>
    /// The chunk's strings without a valid stored translation → why (<c>null</c> = not translated yet, asked for the
    /// first time); <paramref name="warnings"/> = stored translations with warnings only → the first warning's rule.
    /// </summary>
    private static Dictionary<string, string?> Problems(GenerationStepContext context, IReadOnlyList<ExtractedString> chunk, TextMarkup markup, out Dictionary<string, string> warnings)
    {
        var stored = Store(context);
        var problems = new Dictionary<string, string?>(StringComparer.Ordinal);
        warnings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in chunk)
        {
            if (!stored.Strings.TryGetValue(s.Key, out var text) || string.IsNullOrEmpty(text))
            {
                problems[s.Key] = null;
                continue;
            }

            var issues = StringValidator.Validate(s, text, markup);
            var errors = issues.Where(i => i.Severity == StringIssueSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                problems[s.Key] = $"the stored translation \"{text}\" is refused: {string.Join(" ", errors.Select(i => i.Message))}";
            }
            else if (issues.Count > 0)
            {
                warnings[s.Key] = issues[0].Rule;
            }
        }

        return problems;
    }

    /// <summary>Removes stored translations with errors (put_translations refuses them, so only a damaged store has any); returns the store.</summary>
    private static StringTable RemoveRefused(GenerationStepContext context, Dictionary<string, string?> problems)
    {
        var stored = Store(context);
        var refused = problems.Where(p => p.Value is not null && stored.Strings.ContainsKey(p.Key)).Select(p => p.Key).ToList();
        if (refused.Count == 0)
        {
            return stored;
        }

        var strings = stored.Strings.Where(p => !refused.Contains(p.Key)).ToDictionary(StringComparer.Ordinal);
        var notes = stored.Context.Where(p => !refused.Contains(p.Key)).ToDictionary(StringComparer.Ordinal);
        var table = new StringTable(stored.Language, strings, notes);
        context.Files.WriteStrings(table);
        context.Log($"Removed {refused.Count} stored translations that fail validation: {string.Join(", ", refused)}.");
        return table;
    }

    private static StringTable Store(GenerationStepContext context)
    {
        var table = context.Files.ReadStrings() ?? new StringTable(context.Job.Language, new Dictionary<string, string>(), new Dictionary<string, string>());
        return table.Language == context.Job.Language
            ? table
            : throw new GenerationFileException(context.Files.StringsPath, $"the store is in '{table.Language}', not '{context.Job.Language}'");
    }

    private static Glossary? Glossary(GenerationStepContext context)
    {
        try
        {
            return context.Files.ReadGlossary() is { } g && g.Language == context.Job.Language ? g : null;
        }
        catch (GenerationFileException e)
        {
            context.Log($"The glossary can't be read, translating without it: {e.Message}");
            return null;
        }
    }

    private static GenerationStepResult.Failed Fail(GenerationStepContext context, GenerationFailureKind kind, string message)
    {
        context.Log(message);
        return new GenerationStepResult.Failed(kind, message);
    }

    internal static Dictionary<string, string> Values(
        string gameName,
        string language,
        SourceStrings source,
        int number,
        int count,
        IReadOnlyList<ExtractedString> strings,
        IReadOnlyList<GlossaryEntry> entries,
        string? tone,
        IReadOnlyDictionary<string, string?>? problems) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["engine"] = source.Engine.DisplayName,
        ["languageName"] = PromptValues.LanguageName(language),
        ["chunkNumber"] = number.ToString("N0", CultureInfo.InvariantCulture),
        ["chunkCount"] = count.ToString("N0", CultureInfo.InvariantCulture),
        ["stringCount"] = strings.Count.ToString("N0", CultureInfo.InvariantCulture),
        ["tone"] = string.IsNullOrWhiteSpace(tone) ? "not set; keep it consistent with the context of the strings." : tone.Trim(),
        ["glossary"] = GlossaryLines(entries),
        ["strings"] = StringLines(strings, problems),
    };

    /// <summary><c>- "Potion" → "Зелье" (noun, neuter): a healing drink</c>, one per entry; <c>(none)</c> without any.</summary>
    internal static string GlossaryLines(IReadOnlyList<GlossaryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "(none)";
        }

        var text = new StringBuilder();
        foreach (var e in entries)
        {
            text.Append(text.Length > 0 ? "\n" : "").Append("- \"").Append(e.Term).Append('"');
            text.Append(e.KeepAsIs ? ": keep as is" : $" → \"{e.Translation}\"");
            var grammar = string.Join(", ", new[] { e.PartOfSpeech, e.Gender }.Where(g => !string.IsNullOrWhiteSpace(g)));
            if (grammar.Length > 0)
            {
                text.Append(" (").Append(grammar).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(e.Note))
            {
                text.Append(": ").Append(e.Note.ReplaceLineEndings(" ").Trim());
            }
        }

        return text.ToString();
    }

    /// <summary>One JSON object per string: key, original, context, maxLength (+ unit when not characters), placeholders, problem.</summary>
    internal static string StringLines(IReadOnlyList<ExtractedString> strings, IReadOnlyDictionary<string, string?>? problems)
    {
        var lines = new List<string>();
        foreach (var s in strings)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, LineOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("key", s.Key);
                writer.WriteString("original", s.Original);
                if (s.Context is { } note)
                {
                    writer.WriteString("context", note);
                }

                if (s.MaxLength is { } max)
                {
                    writer.WriteNumber("maxLength", max);
                    if (s.MaxLengthUnit == LengthUnit.DisplayWidth)
                    {
                        writer.WriteString("maxLengthUnit", "display width: wide characters count 2");
                    }
                }

                if (s.Placeholders.Count > 0)
                {
                    writer.WriteStartArray("placeholders");
                    foreach (var p in s.Placeholders)
                    {
                        writer.WriteStringValue(p);
                    }

                    writer.WriteEndArray();
                }

                if (problems is not null && problems.TryGetValue(s.Key, out var problem))
                {
                    writer.WriteString("problem", problem ?? "no translation was stored for it");
                }

                writer.WriteEndObject();
            }

            lines.Add(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return string.Join('\n', lines);
    }
}
