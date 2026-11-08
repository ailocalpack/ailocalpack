using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline;

/// <summary>A string just translated, for the "just translated" feed.</summary>
public sealed record RecentTranslation(string Key, string Original, string Translation);

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

/// <summary>What a running job shows besides its counters (task 50; the Generation tab is task 53).</summary>
/// <param name="Recent">The last strings the Translate stage stored (at most <see cref="PipelineProgressFile.RecentCount"/>), oldest first.</param>
/// <param name="Usage">The agent's usage so far.</param>
public sealed record PipelineProgress(IReadOnlyList<RecentTranslation> Recent, UsageTotals Usage)
{
    public static PipelineProgress Empty { get; } = new([], UsageTotals.None);
}

/// <summary>
/// <c>progress.json</c> in the job folder (<see cref="GenerationFiles.ProgressPath"/>): <c>{schemaVersion: 1, recent:
/// [{key, original, translation}], usage: {runs, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens,
/// costUsd?}}</c>. Best effort both ways: it only feeds the display, so a missing or broken file reads as empty and a
/// failed write is ignored.
/// </summary>
public static class PipelineProgressFile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Lines of the "just translated" feed.</summary>
    public const int RecentCount = 3;

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
                .Select(r => new RecentTranslation(r!.Key!, r.Original!, r.Translation!))
                .TakeLast(RecentCount)
                .ToList();
            var u = json.Usage;
            var usage = u is null
                ? UsageTotals.None
                : new UsageTotals(u.Runs, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CostUsd);
            return new PipelineProgress(recent, usage);
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
        var next = change(Read(files));
        var usage = next.Usage;
        var json = new ProgressFileJson(
            CurrentSchemaVersion,
            [.. next.Recent.TakeLast(RecentCount).Select(r => new RecentJson(r.Key, r.Original, r.Translation))],
            new UsageJson(usage.Runs, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.CostUsd));
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

internal sealed record ProgressFileJson(int SchemaVersion, IReadOnlyList<RecentJson?>? Recent, UsageJson? Usage);

internal sealed record RecentJson(string? Key, string? Original, string? Translation);

internal sealed record UsageJson(int Runs, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? CostUsd);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProgressFileJson))]
internal sealed partial class ProgressJsonContext : JsonSerializerContext;
