using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// A limit the agent learned about the game (decided 2026-11-17 after Barony, which overruns a fixed-size buffer with
/// long UTF-8 text): the strings whose keys match <see cref="Keys"/> hold at most <see cref="MaxBytes"/> bytes in UTF-8,
/// markup included. Every later translation of those strings is checked against it (<c>too-many-bytes</c>, an error).
/// </summary>
/// <param name="Keys">A key, or a pattern where <c>*</c> stands for any text (<c>lang/en.txt:*</c>).</param>
/// <param name="MaxBytes">The most UTF-8 bytes a translation may take.</param>
/// <param name="Reason">Why, in English (what the agent found), shown with the refusal.</param>
public sealed partial record StringLimit(string Keys, int MaxBytes, string Reason)
{
    /// <summary>Whether <paramref name="key"/> is one of the strings this limit applies to.</summary>
    public bool Matches(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Keys.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(Keys, key, StringComparison.Ordinal);
        }

        var pattern = "^" + string.Join(".*", Keys.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(key, pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }

    /// <summary>The smallest limit of <paramref name="limits"/> that applies to <paramref name="key"/>, or <c>null</c>.</summary>
    public static StringLimit? For(IReadOnlyList<StringLimit> limits, string key)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return limits.Where(l => l.Matches(key)).MinBy(l => l.MaxBytes);
    }
}

/// <summary>
/// <c>limits.json</c> of a job folder: <c>{schemaVersion: 1, limits: [{keys, maxBytes, reason}]}</c>, written atomically;
/// set by the agent (<c>put_limit</c>) while it repairs or fixes the translation. Never throws for a missing or broken
/// file (no limits); a limit with the same <c>keys</c> replaces the earlier one.
/// </summary>
public static class StringLimitsFile
{
    public const int SchemaVersion = 1;

    public const string FileName = "limits.json";

    /// <summary>The most limits a job keeps.</summary>
    public const int MaxLimits = 100;

    public static string PathIn(string jobFolder) => Path.Combine(jobFolder, FileName);

    /// <summary>The job's limits (empty when none, or the file can't be read).</summary>
    public static IReadOnlyList<StringLimit> Read(string jobFolder)
    {
        try
        {
            var path = PathIn(jobFolder);
            if (!File.Exists(path))
            {
                return [];
            }

            var json = JsonSerializer.Deserialize(File.ReadAllBytes(path), StringLimitsJsonContext.Default.StringLimitsJson);
            return json is { SchemaVersion: SchemaVersion, Limits: { } limits }
                ? [.. limits.Where(l => l is { Keys.Length: > 0, MaxBytes: > 0 }).Select(l => new StringLimit(l!.Keys!, l.MaxBytes, l.Reason ?? string.Empty))]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds or replaces the limit with its <see cref="StringLimit.Keys"/>; <c>maxBytes</c> 0 removes it. Returns the limits now.</summary>
    /// <exception cref="IOException">The file can't be written.</exception>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxLimits"/> limits.</exception>
    public static IReadOnlyList<StringLimit> Put(string jobFolder, StringLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        var limits = Read(jobFolder).Where(l => !string.Equals(l.Keys, limit.Keys, StringComparison.Ordinal)).ToList();
        if (limit.MaxBytes > 0)
        {
            limits.Add(limit);
        }

        if (limits.Count > MaxLimits)
        {
            throw new InvalidOperationException($"A job keeps at most {MaxLimits} limits: use a pattern with * for many keys.");
        }

        var json = new StringLimitsJson(SchemaVersion, [.. limits.Select(l => new StringLimitJson(l.Keys, l.MaxBytes, l.Reason))]);
        AtomicFile.WriteAllBytes(PathIn(jobFolder), JsonSerializer.SerializeToUtf8Bytes(json, StringLimitsJsonContext.Default.StringLimitsJson));
        return limits;
    }
}

internal sealed record StringLimitsJson(int SchemaVersion, IReadOnlyList<StringLimitJson?>? Limits);

internal sealed record StringLimitJson(string? Keys, int MaxBytes, string? Reason);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StringLimitsJson))]
internal sealed partial class StringLimitsJsonContext : JsonSerializerContext;
