using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>Words shared by the Generation tab, the page header, the library card and the Activity panel (task 53).</summary>
public static class GenerationTexts
{
    /// <summary>"25 min", "2 h 10 min", "3 h": whole minutes, at least one.</summary>
    public static string Duration(TimeSpan span, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        if (minutes < 60)
        {
            return localizer.Format(nameof(Strings.Gen_Duration_Minutes), minutes);
        }

        var (hours, rest) = Math.DivRem(minutes, 60);
        return rest == 0
            ? localizer.Format(nameof(Strings.Gen_Duration_HoursExact), hours)
            : localizer.Format(nameof(Strings.Gen_Duration_Hours), hours, rest);
    }

    /// <summary>"18:00" today in the player's zone, else with the day ("Thu 18:00").</summary>
    public static string ClockTime(DateTimeOffset at, TimeProvider time, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(localizer);
        var zone = time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).Date;
        var clock = local.ToString("t", localizer.Culture);
        return local.Date == today ? clock : $"{local.ToString("ddd", localizer.Culture)} {clock}";
    }

    /// <summary>"Extract", "Build &amp; test": the step's name in the five-step header.</summary>
    public static string StepName(GenerationStage stage, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[stage switch
        {
            GenerationStage.Setup => nameof(Strings.Gen_Step_Setup),
            GenerationStage.Extract => nameof(Strings.Gen_Step_Extract),
            GenerationStage.Glossary => nameof(Strings.Gen_Step_Glossary),
            GenerationStage.Translate => nameof(Strings.Gen_Step_Translate),
            _ => nameof(Strings.Gen_Step_Build),
        }];
    }

    /// <summary>"Translating", "Building the glossary": what the job does in the stage.</summary>
    public static string StageName(GenerationStage stage, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[stage switch
        {
            GenerationStage.Setup => nameof(Strings.Gen_Stage_Setup),
            GenerationStage.Extract => nameof(Strings.Gen_Stage_Extract),
            GenerationStage.Glossary => nameof(Strings.Gen_Stage_Glossary),
            GenerationStage.Translate => nameof(Strings.Gen_Stage_Translate),
            _ => nameof(Strings.Gen_Stage_Build),
        }];
    }

    /// <summary>The failure in plain words with what to do (the kind; the English message stays for Details).</summary>
    public static string Failure(GenerationFailure failure, string agentName, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(localizer);
        return failure.Kind switch
        {
            GenerationFailureKind.Agent => localizer[nameof(Strings.Gen_Failure_Agent)],
            GenerationFailureKind.SignedOut => localizer.Format(nameof(Strings.Gen_Failure_SignedOut), agentName),
            GenerationFailureKind.AgentMissing => localizer.Format(nameof(Strings.Gen_Failure_AgentMissing), agentName),
            GenerationFailureKind.Toolkit => localizer[nameof(Strings.Gen_Failure_Toolkit)],
            GenerationFailureKind.WorkingCopy => localizer[nameof(Strings.Gen_Failure_WorkingCopy)],
            GenerationFailureKind.Disk => localizer[nameof(Strings.Gen_Failure_Disk)],
            GenerationFailureKind.TestRun => localizer[nameof(Strings.Gen_Failure_TestRun)],
            _ => localizer[nameof(Strings.Gen_Failure_Unexpected)],
        };
    }

    /// <summary>A validator rule as a short tag: "same as original", "spaces", "line breaks".</summary>
    public static string Rule(string rule, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[rule switch
        {
            StringValidator.Untranslated => nameof(Strings.Gen_Warning_Rule_Untranslated),
            StringValidator.Whitespace => nameof(Strings.Gen_Warning_Rule_Whitespace),
            StringValidator.LineBreaks => nameof(Strings.Gen_Warning_Rule_LineBreaks),
            _ => nameof(Strings.Gen_Warning_Rule_Unknown),
        }];
    }
}
