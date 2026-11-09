using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>The tone of a result tile's icon: ✓ green, ⚠ amber, ⓘ accent.</summary>
public enum ResultTone
{
    Ok,
    Warn,
    Info,
}

/// <summary>One tile of the Generation result (design §6d): what it is about, the value with its icon, a line under it.</summary>
public sealed record ResultTile(string Title, string Value, string Detail, ResultTone Tone)
{
    public bool IsOk => Tone == ResultTone.Ok;

    public bool IsWarn => Tone == ResultTone.Warn;

    public bool IsInfo => Tone == ResultTone.Info;

    /// <summary>Resource key of the value's icon.</summary>
    public string Icon => Tone switch
    {
        ResultTone.Ok => "IconCheck",
        ResultTone.Warn => "IconAlert",
        _ => "IconInfo",
    };
}

/// <summary>
/// The words of a pack generated on this machine (task 55), shared by the Generation tab's result card and Packs → Mine:
/// the four tiles (coverage, test run, fonts, validator) from the build report, the agent, the size.
/// </summary>
public static class LocalPackTexts
{
    /// <summary>Coverage at or above this share is a ✓ (a few strings left in the original are expected: debug, credits).</summary>
    public const double GoodCoverage = 0.95;

    /// <summary>Coverage, test run, fonts, validator.</summary>
    public static IReadOnlyList<ResultTile> Tiles(LocalPack pack, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(localizer);
        return [Coverage(pack, localizer), TestRun(pack.Report.TestRun, localizer), Fonts(pack.Report.Fonts, localizer), Validator(pack.Report, localizer)];
    }

    /// <summary>"12,370 / 12,384" + "14 strings stay untranslated" or "Every string is translated".</summary>
    public static ResultTile Coverage(LocalPack pack, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(localizer);
        var coverage = pack.Manifest.Coverage ?? new PackCoverage(0, 0);
        var untranslated = pack.Report.Untranslated.Count;
        return new ResultTile(
            localizer[nameof(Strings.Gen_Result_Tile_Coverage)],
            localizer.Format(nameof(Strings.Gen_Result_Coverage_Value), coverage.Translated.ToString("N0", localizer.Culture), coverage.Total.ToString("N0", localizer.Culture)),
            untranslated > 0
                ? localizer.Plural(nameof(Strings.Gen_Result_Coverage_Untranslated_One), untranslated, untranslated.ToString("N0", localizer.Culture))
                : localizer[nameof(Strings.Gen_Result_Coverage_All)],
            coverage.Ratio >= GoodCoverage ? ResultTone.Ok : ResultTone.Warn);
    }

    /// <summary>"Passed" + "The translated game ran 15 s without closing", or "Skipped" + why.</summary>
    public static ResultTile TestRun(TestRunResult run, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[nameof(Strings.Gen_Result_Tile_TestRun)];
        return run.Outcome switch
        {
            TestRunOutcome.Passed => new ResultTile(
                title,
                localizer[nameof(Strings.Gen_Result_TestRun_Passed)],
                localizer.Format(nameof(Strings.Gen_Result_TestRun_PassedDetail), Math.Round((run.Ran ?? Pipeline.TestRunOptions.Default.Watch).TotalSeconds).ToString("0", localizer.Culture)),
                ResultTone.Ok),
            TestRunOutcome.Crashed => new ResultTile(title, localizer[nameof(Strings.Gen_Result_TestRun_Crashed)], run.Detail ?? string.Empty, ResultTone.Warn),
            _ => new ResultTile(title, localizer[nameof(Strings.Gen_Result_TestRun_Skipped)], SkipReason(run.Skip, localizer), ResultTone.Info),
        };
    }

    /// <summary>Why the test run was skipped, in plain words.</summary>
    public static string SkipReason(TestRunSkip? skip, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[skip switch
        {
            TestRunSkip.NoExecutable => nameof(Strings.Gen_Result_Skip_NoExecutable),
            TestRunSkip.OtherPlatform => nameof(Strings.Gen_Result_Skip_OtherPlatform),
            TestRunSkip.SteamDrm => nameof(Strings.Gen_Result_Skip_SteamDrm),
            TestRunSkip.StartFailed => nameof(Strings.Gen_Result_Skip_StartFailed),
            TestRunSkip.ClosesUntranslated => nameof(Strings.Gen_Result_Skip_ClosesUntranslated),
            _ => nameof(Strings.Gen_Result_Skip_TurnedOff),
        }];
    }

    /// <summary>"Noto Sans swapped in" + which fonts lacked letters; "Game fonts fit"; "Not checked".</summary>
    public static ResultTile Fonts(LocalPackFonts fonts, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[nameof(Strings.Gen_Result_Tile_Fonts)];
        if (!fonts.Checked)
        {
            return new ResultTile(title, localizer[nameof(Strings.Gen_Result_Fonts_NotChecked)], localizer[nameof(Strings.Gen_Result_Fonts_NotCheckedDetail)], ResultTone.Info);
        }

        if (fonts.Replacement is not { } replacement)
        {
            return new ResultTile(title, localizer[nameof(Strings.Gen_Result_Fonts_Fit)], localizer[nameof(Strings.Gen_Result_Fonts_FitDetail)], ResultTone.Ok);
        }

        var name = FontWhitelist.Bundled.Find(replacement)?.Name ?? replacement;
        var detail = fonts.Uncovered.Count > 0
            ? localizer.Plural(nameof(Strings.Gen_Result_Fonts_Uncovered_One), fonts.Uncovered.Count, fonts.Uncovered.Count.ToString("N0", localizer.Culture))
            : fonts.Swapped.Count > 0
                ? localizer.Format(nameof(Strings.Gen_Result_Fonts_SwappedDetail), string.Join(", ", fonts.Swapped.Select(f => $"“{f}”")))
                : string.Empty;
        return new ResultTile(title, localizer.Format(nameof(Strings.Gen_Result_Fonts_Swapped), name), detail, fonts.Uncovered.Count > 0 ? ResultTone.Warn : ResultTone.Info);
    }

    /// <summary>"3 warnings" + "spaces: 2 · line breaks: 1", or "No warnings".</summary>
    public static ResultTile Validator(LocalPackReport report, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[nameof(Strings.Gen_Result_Tile_Validator)];
        var count = report.Warnings.Count;
        if (count == 0)
        {
            return new ResultTile(title, localizer[nameof(Strings.Gen_Result_Validator_None)], localizer[nameof(Strings.Gen_Result_Validator_NoneDetail)], ResultTone.Ok);
        }

        var rules = report.Warnings
            .GroupBy(w => GenerationTexts.Rule(w.Rule, localizer))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => localizer.Format(nameof(Strings.Gen_Result_Validator_Rule), g.Key, g.Count().ToString("N0", localizer.Culture)));
        return new ResultTile(
            title,
            localizer.Plural(nameof(Strings.Gen_Result_Validator_Count_One), count, count.ToString("N0", localizer.Culture)),
            string.Join(" · ", rules),
            ResultTone.Warn);
    }

    /// <summary>"Claude Code (claude-opus-5-5)", "Codex", "By hand".</summary>
    public static string Agent(PackManifest manifest, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(localizer);
        return manifest.Agent switch
        {
            null => localizer[nameof(Strings.Packs_Agent_ByHand)],
            { Model: { } model } agent => $"{AgentName(agent.Id)} ({model})",
            { } agent => AgentName(agent.Id),
        };
    }

    /// <summary>"Claude Code" for the manifest's agent ID.</summary>
    public static string AgentName(string id) => id switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        _ => id,
    };

    /// <summary>"98% of strings", rounded down (only a full pack says 100%).</summary>
    public static string CoverageText(PackManifest manifest, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format(nameof(Strings.Packs_Coverage), Math.Floor((manifest.Coverage?.Ratio ?? 0) * 100).ToString("0", localizer.Culture));
    }

    /// <summary>The validator rows (key, rule, original → translation), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<GenerationWarningRow> Warnings(LocalPackReport report, Localizer localizer, int max = 200)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);
        return [.. report.Warnings.Take(max).Select(w => new GenerationWarningRow(w.Key, GenerationTexts.Rule(w.Rule, localizer), w.Original, w.Translation))];
    }
}
