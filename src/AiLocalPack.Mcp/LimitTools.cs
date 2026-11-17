using System.ComponentModel;
using System.Text;
using AiLocalPack.Core.Generation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiLocalPack.Mcp;

/// <summary>A limit as the tools show it.</summary>
public sealed record LimitItem(string Keys, int MaxBytes, string Reason, int Strings);

/// <summary>A stored translation over a limit.</summary>
public sealed record OverLimit(string Key, int Bytes, int MaxBytes, string Translation);

/// <param name="Limits">Every limit of the job now.</param>
/// <param name="Over">Stored translations over a limit now (the first <see cref="LimitTools.ListedOver"/>).</param>
/// <param name="OverCount">How many stored translations are over a limit.</param>
public sealed record LimitsResult(IReadOnlyList<LimitItem> Limits, IReadOnlyList<OverLimit> Over, int OverCount);

/// <summary>
/// <c>get_limits</c> and <c>put_limit</c>, offered with <c>test_run</c> (a fix or a repair of a crash): limits the agent
/// learned about the game (<see cref="StringLimit"/>, the job's <c>limits.json</c>): a byte limit on the strings whose keys
/// match a pattern. From then on <c>put_translations</c> refuses a translation over it (<c>too-many-bytes</c>), and the
/// Build stage leaves such a stored translation out, so a later translation can't crash the game the same way.
/// </summary>
[McpServerToolType]
public sealed class LimitTools(ToolkitSession session)
{
    public const int ListedOver = 50;

    [McpServerTool(Name = "get_limits", ReadOnly = true, Idempotent = true)]
    [Description("The limits learned about this game (UTF-8 bytes a translation may take, by key pattern) and the stored translations over them now.")]
    public LimitsResult GetLimits(CancellationToken cancellationToken = default) => Result(StringLimitsFile.Read(session.Files.Folder), cancellationToken);

    [McpServerTool(Name = "put_limit", Destructive = false, Idempotent = true)]
    [Description("Records a limit you found in the game: the strings whose keys match `keys` (a key, or a pattern where * is any text, e.g. `lang/en.txt:*`) "
        + "may take at most `maxBytes` bytes in UTF-8, markup included (a fixed-size buffer the game copies them into). From then on put_translations refuses "
        + "a translation over it and the app leaves one out of the build, so set it only once a test run showed it. maxBytes 0 removes the limit with these keys. "
        + "Returns every limit and the stored translations over them now: shorten those.")]
    public LimitsResult PutLimit(
        [Description("A key, or a pattern with * for any text.")] string keys,
        [Description("The most UTF-8 bytes a translation may take (0 removes the limit).")] int maxBytes,
        [Description("What you found, in English (e.g. 'item names are copied into a 64-byte buffer; longer ones crash the game at start').")] string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keys) || keys.Trim('*').Length == 0)
        {
            throw new McpException("keys must name a key or a pattern with more than *.");
        }

        if (maxBytes is < 0 or > 1_000_000)
        {
            throw new McpException("maxBytes must be between 0 (remove) and 1000000.");
        }

        if (maxBytes > 0 && string.IsNullOrWhiteSpace(reason))
        {
            throw new McpException("Say in reason what you found, so the limit can be understood later.");
        }

        IReadOnlyList<StringLimit> limits;
        lock (session.Gate)
        {
            try
            {
                limits = StringLimitsFile.Put(session.Files.Folder, new StringLimit(keys.Trim(), maxBytes, reason?.Trim() ?? string.Empty));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new McpException($"The limit can't be saved: {e.Message}", e);
            }
        }

        return Result(limits, cancellationToken);
    }

    private LimitsResult Result(IReadOnlyList<StringLimit> limits, CancellationToken cancellationToken)
    {
        var keys = session.Extract(cancellationToken: cancellationToken).Result.Strings.Select(s => s.Key).ToList();
        var stored = session.ReadStrings().Strings;
        var over = new List<OverLimit>();
        foreach (var (key, text) in stored.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (StringLimit.For(limits, key) is { } limit && Encoding.UTF8.GetByteCount(text) is var bytes && bytes > limit.MaxBytes)
            {
                over.Add(new OverLimit(key, bytes, limit.MaxBytes, text));
            }
        }

        return new LimitsResult(
            [.. limits.Select(l => new LimitItem(l.Keys, l.MaxBytes, l.Reason, keys.Count(l.Matches)))],
            [.. over.Take(ListedOver)],
            over.Count);
    }
}
