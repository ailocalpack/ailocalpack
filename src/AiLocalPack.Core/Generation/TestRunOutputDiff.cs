using System.Text.RegularExpressions;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// Where a crashed translated game's output parted from the untranslated game's (the same program printing the same
/// steps as it loads): the last line both printed, what the untranslated game printed next (the step the translation
/// never reached: often the file it was reading), and the lines only the translation printed (its own complaints).
/// </summary>
/// <param name="LastShared">The translated game's last line the untranslated game printed too; <c>null</c> = none in common.</param>
/// <param name="OriginalNext">What the untranslated game printed after it (at most <see cref="MaxNext"/> lines).</param>
/// <param name="OnlyTranslated">Lines the untranslated game never printed (at most <see cref="MaxOnly"/>, the last ones).</param>
/// <param name="TranslatedLines">How many lines the translated game printed.</param>
/// <param name="OriginalLines">How many lines the untranslated game printed.</param>
public sealed partial record TestRunOutputDiff(
    string? LastShared,
    IReadOnlyList<string> OriginalNext,
    IReadOnlyList<string> OnlyTranslated,
    int TranslatedLines,
    int OriginalLines)
{
    public const int MaxNext = 5;
    public const int MaxOnly = 10;

    /// <summary>How far ahead a line is looked for in the untranslated output (threads print in a different order run to run).</summary>
    private const int Window = 400;

    /// <summary>
    /// Compares the two outputs line by line with numbers ignored (times, counts, ids differ run to run), walking both in
    /// order; <c>null</c> when the untranslated game printed nothing to compare with.
    /// </summary>
    public static TestRunOutputDiff? Compare(IReadOnlyList<string> original, IReadOnlyList<string> translated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(translated);
        var originalKeys = original.Select(Normalize).ToList();
        if (originalKeys.All(k => k.Length == 0))
        {
            return null;
        }

        var known = originalKeys.Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var only = new List<string>();
        var at = -1;
        string? shared = null;
        foreach (var line in translated)
        {
            var key = Normalize(line);
            if (key.Length == 0)
            {
                continue;
            }

            if (!known.Contains(key))
            {
                only.Add(line.Trim());
                continue;
            }

            var end = Math.Min(originalKeys.Count, at + 1 + Window);
            for (var i = at + 1; i < end; i++)
            {
                if (originalKeys[i] == key)
                {
                    at = i;
                    shared = line.Trim();
                    break;
                }
            }
        }

        var next = original.Skip(at + 1).Select(l => l.Trim()).Where(l => l.Length > 0).Take(MaxNext).ToList();
        return new TestRunOutputDiff(shared, next, only.TakeLast(MaxOnly).ToList(), translated.Count, original.Count);
    }

    /// <summary>One plain English sentence or two for the failure's detail; <c>null</c> when there is nothing to tell.</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        if (TranslatedLines == 0)
        {
            parts.Add($"The translated game printed nothing, while the untranslated game printed {OriginalLines} lines.");
        }
        else if (LastShared is null)
        {
            parts.Add("The translated game's output has no line in common with the untranslated game's.");
        }
        else if (OriginalNext.Count == 0)
        {
            parts.Add($"The translated game printed everything the untranslated game did, up to \"{Cut(LastShared)}\".");
        }
        else
        {
            parts.Add($"The translated game's output stopped after \"{Cut(LastShared)}\"; the untranslated game went on with "
                + string.Join(" | ", OriginalNext.Take(3).Select(l => $"\"{Cut(l)}\"")) + ".");
        }

        if (OnlyTranslated.Count > 0)
        {
            parts.Add("Only the translated game printed: " + string.Join(" | ", OnlyTranslated.TakeLast(3).Select(l => $"\"{Cut(l)}\"")) + ".");
        }

        return string.Join(' ', parts);
    }

    private static string Cut(string line) => line.Length <= 160 ? line : line[..157] + "...";

    private static string Normalize(string line) => Digits().Replace(line, "#").Trim();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

/// <summary>The fault the OS recorded for a crash (Windows Application Error event 1000).</summary>
/// <param name="Module">The module the fault happened in (<c>barony.exe</c>, <c>ucrtbase.dll</c>).</param>
/// <param name="InGame">The module is the game's own program, not a system or driver library.</param>
public sealed record CrashFault(string Module, string? ModuleVersion, string? ExceptionCode, string? Offset, bool InGame)
{
    /// <summary>Plain English for the failure's detail.</summary>
    public string Describe() =>
        $"Windows recorded the fault in {Module}" + (InGame ? " (the game's own code)" : "")
        + (Offset is { } offset ? $" at offset {offset}" : "")
        + (ExceptionCode is { } code ? $", exception {code}" : "") + ".";
}
