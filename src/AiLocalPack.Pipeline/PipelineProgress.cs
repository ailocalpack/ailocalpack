using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline;

/// <summary>A string just translated, for the "just translated" feed.</summary>
/// <param name="Warning">The validator's warning about it (its rule, <c>untranslated</c>, <c>whitespace</c>, <c>line-breaks</c>), if any.</param>
public sealed record RecentTranslation(string Key, string Original, string Translation, string? Warning = null);

/// <summary>A stored translation the validator warns about (task 53: the progress card's validator box).</summary>
/// <param name="Rule">The rule (<see cref="Toolkit.Validation.StringValidator"/>: <c>untranslated</c>, <c>whitespace</c>, <c>line-breaks</c>).</param>
public sealed record TranslationWarning(string Key, string Rule, string Original, string Translation);

/// <summary>The agent's usage window as last reported (<see cref="AgentLimitStatus"/>).</summary>
/// <param name="Used">Share used, 0–1.</param>
public sealed record AgentLimitInfo(double Used, string? Window, DateTimeOffset? ResetsAt);

/// <summary>Where the Build stage is (task 53).</summary>
public enum BuildStep
{
    /// <summary>Checking the stored translations again.</summary>
    Checking,

    /// <summary>Rebuilding the game files from the translations.</summary>
    Rebuilding,

    /// <summary>Checking the game's fonts for the translation's letters (and swapping one in).</summary>
    Fonts,

    /// <summary>Launching the translated copy for the test run.</summary>
    TestRun,

    /// <summary>Saving the local pack.</summary>
    Packing,
}

/// <summary>The Build stage's current step and what it found about fonts so far.</summary>
/// <param name="Files">Game files rebuilt so far (known from <see cref="BuildStep.Fonts"/> on).</param>
/// <param name="SwappedFonts">The game's fonts that lack letters of the translation.</param>
/// <param name="Replacement">The open font swapped in for them.</param>
public sealed record BuildProgress(BuildStep Step, int Files = 0, IReadOnlyList<string>? SwappedFonts = null, string? Replacement = null);

/// <summary>The agent's usage summed over every run of the job.</summary>
/// <param name="Runs">Agent runs that reported usage.</param>
/// <param name="CostUsd">Sum of the costs the agent reported (Claude Code does, Codex doesn't), or <c>null</c> when none did.</param>
public sealed record UsageTotals(int Runs, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? CostUsd)
{
    public static UsageTotals None { get; } = new(0, 0, 0, 0, 0, null);

    /// <summary>These totals plus the usage events of one run.</summary>
    public UsageTotals Add(IReadOnlyList<AgentUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.Count == 0)
        {
            return this;
        }

        var costs = usage.Where(u => u.CostUsd is not null).Select(u => u.CostUsd!.Value).ToList();
        return new UsageTotals(
            Runs + 1,
            InputTokens + usage.Sum(u => u.InputTokens),
            OutputTokens + usage.Sum(u => u.OutputTokens),
            CacheReadTokens + usage.Sum(u => u.CacheReadTokens),
            CacheWriteTokens + usage.Sum(u => u.CacheWriteTokens),
            costs.Count == 0 ? CostUsd : (CostUsd ?? 0) + costs.Sum());
    }
}

/// <summary>The agent program's version and the model its last session used, when known.</summary>
public sealed record AgentRunInfo(string? Version, string? Model);

/// <summary>What a running job shows besides its counters (task 50; the Generation tab is task 53).</summary>
/// <param name="Recent">The last strings the Translate stage stored (at most <see cref="PipelineProgressFile.RecentCount"/>), oldest first.</param>
/// <param name="Usage">The agent's usage so far.</param>
/// <param name="Agent">The agent's version and model as last seen (the Build stage puts them in the manifest, task 51).</param>
public sealed record PipelineProgress(IReadOnlyList<RecentTranslation> Recent, UsageTotals Usage, AgentRunInfo? Agent = null)
{
    public static PipelineProgress Empty { get; } = new([], UsageTotals.None);

    /// <summary>Every stored translation the validator warns about, in the order the chunks stored them (task 53).</summary>
    public IReadOnlyList<TranslationWarning> Warnings { get; init; } = [];

    /// <summary>The agent's usage window as it last reported it (Claude Code only), else <c>null</c>.</summary>
    public AgentLimitInfo? Limit { get; init; }

    /// <summary>The Build stage's step, once it started.</summary>
    public BuildProgress? Build { get; init; }
}

/// <summary>
/// <c>progress.json</c> in the job folder (<see cref="GenerationFiles.ProgressPath"/>): <c>{schemaVersion: 1, recent:
/// [{key, original, translation, warning?}], usage: {runs, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens,
/// costUsd?}, agent?: {version?, model?}, warnings?: [{key, rule, original, translation}], limit?: {used, window?,
/// resetsAt?}, build?: {step, files, swappedFonts?, replacement?}}</c> (step camelCase). Best effort both ways: it only feeds the display, so a missing or broken file reads as empty and a
/// failed write is ignored.
/// </summary>
public static class PipelineProgressFile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Lines of the "just translated" feed.</summary>
    public const int RecentCount = 3;

    private static readonly Lock Gate = new();

    private static readonly JsonSerializerOptions WriteOptions = new(ProgressJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PipelineProgress Read(GenerationFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            if (!File.Exists(files.ProgressPath))
            {
                return PipelineProgress.Empty;
            }

            var json = JsonSerializer.Deserialize(File.ReadAllBytes(files.ProgressPath), ProgressJsonContext.Default.ProgressFileJson);
            if (json is null || json.SchemaVersion != CurrentSchemaVersion)
            {
                return PipelineProgress.Empty;
            }

            var recent = (json.Recent ?? [])
                .Where(r => r is { Key: not null, Original: not null, Translation: not null })
                .Select(r => new RecentTranslation(r!.Key!, r.Original!, r.Translation!, r.Warning))
                .TakeLast(RecentCount)
                .ToList();
            var u = json.Usage;
            var usage = u is null
                ? UsageTotals.None
                : new UsageTotals(u.Runs, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CostUsd);
            var agent = json.Agent is { } a ? new AgentRunInfo(a.Version, a.Model) : null;
            var warnings = (json.Warnings ?? [])
                .Where(w => w is { Key: not null, Rule: not null, Original: not null, Translation: not null })
                .Select(w => new TranslationWarning(w!.Key!, w.Rule!, w.Original!, w.Translation!))
                .ToList();
            var limit = json.Limit is { } l ? new AgentLimitInfo(Math.Clamp(l.Used, 0, 1), l.Window, l.ResetsAt) : null;
            var build = json.Build is { Step: { } step } b && Enum.TryParse<BuildStep>(step, ignoreCase: true, out var parsed)
                ? new BuildProgress(parsed, b.Files, b.SwappedFonts, b.Replacement)
                : null;
            return new PipelineProgress(recent, usage, agent) { Warnings = warnings, Limit = limit, Build = build };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return PipelineProgress.Empty;
        }
    }

    /// <summary>Changes the stored progress (read, <paramref name="change"/>, write); never throws for disk state.</summary>
    public static PipelineProgress Update(GenerationFiles files, Func<PipelineProgress, PipelineProgress> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        // Translate chunks running side by side update it at once: one read, change and write at a time.
        lock (Gate)
        {
            return Write(files, change(Read(files)));
        }
    }

    private static PipelineProgress Write(GenerationFiles files, PipelineProgress next)
    {
        var usage = next.Usage;
        var json = new ProgressFileJson(
            CurrentSchemaVersion,
            [.. next.Recent.TakeLast(RecentCount).Select(r => new RecentJson(r.Key, r.Original, r.Translation, r.Warning))],
            new UsageJson(usage.Runs, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.CostUsd),
            next.Agent is { } agent ? new AgentJson(agent.Version, agent.Model) : null,
            next.Warnings.Count == 0 ? null : [.. next.Warnings.Select(w => new WarningJson(w.Key, w.Rule, w.Original, w.Translation))],
            next.Limit is { } limit ? new LimitJson(limit.Used, limit.Window, limit.ResetsAt) : null,
            next.Build is { } build ? new BuildJson(JsonNamingPolicy.CamelCase.ConvertName(build.Step.ToString()), build.Files, build.SwappedFonts, build.Replacement) : null);
        try
        {
            AtomicFile.WriteAllBytes(files.ProgressPath, JsonSerializer.SerializeToUtf8Bytes(json, WriteOptions.GetTypeInfo(typeof(ProgressFileJson))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Display only.
        }

        return next;
    }
}

internal sealed record ProgressFileJson(
    int SchemaVersion,
    IReadOnlyList<RecentJson?>? Recent,
    UsageJson? Usage,
    AgentJson? Agent = null,
    IReadOnlyList<WarningJson?>? Warnings = null,
    LimitJson? Limit = null,
    BuildJson? Build = null);

internal sealed record AgentJson(string? Version, string? Model);

internal sealed record RecentJson(string? Key, string? Original, string? Translation, string? Warning = null);

internal sealed record WarningJson(string? Key, string? Rule, string? Original, string? Translation);

internal sealed record LimitJson(double Used, string? Window, DateTimeOffset? ResetsAt);

internal sealed record BuildJson(string? Step, int Files, IReadOnlyList<string>? SwappedFonts, string? Replacement);

internal sealed record UsageJson(int Runs, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? CostUsd);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProgressFileJson))]
internal sealed partial class ProgressJsonContext : JsonSerializerContext;
