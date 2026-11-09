using AiLocalPack.Core.Activity;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.Activity;

/// <summary>The Activity panel's words, from an event's kind and data, in the current UI language.</summary>
public static class ActivityTexts
{
    /// <summary>"Installed «Фонарщики»", "The game updated; «Фонарщики» rev 2 is incompatible now"…</summary>
    public static string Title(ActivityEvent activity, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(localizer);
        var key = activity.Kind switch
        {
            ActivityEventKind.PackInstalled => nameof(Strings.Activity_Event_PackInstalled),
            ActivityEventKind.PackUninstalled => nameof(Strings.Activity_Event_PackUninstalled),
            ActivityEventKind.PackReapplied => nameof(Strings.Activity_Event_PackReapplied),
            ActivityEventKind.PackAutoReapplied => nameof(Strings.Activity_Event_PackAutoReapplied),
            ActivityEventKind.InstallFailed => nameof(Strings.Activity_Event_InstallFailed),
            ActivityEventKind.UninstallFailed => nameof(Strings.Activity_Event_UninstallFailed),
            ActivityEventKind.ReapplyFailed => nameof(Strings.Activity_Event_ReapplyFailed),
            ActivityEventKind.AutoReapplyFailed => nameof(Strings.Activity_Event_AutoReapplyFailed),
            ActivityEventKind.PackIncompatible => nameof(Strings.Activity_Event_PackIncompatible),
            ActivityEventKind.GenerationPaused => nameof(Strings.Activity_Event_GenerationPaused),
            _ => nameof(Strings.Activity_Event_PackNotApplied),
        };
        return localizer.Format(key, activity.PackTitle ?? activity.PackId ?? string.Empty, activity.Revision ?? 0);
    }

    /// <summary>
    /// The line under the title: a failure's reason (+ that restoring failed), what an incompatible pack can do
    /// (<paramref name="laterRevision"/> = the registry's newer revision of the same pack), or <c>null</c>.
    /// </summary>
    /// <param name="time">The clock for a paused generation's resume time (the player's zone); the system's when not given.</param>
    public static string? Detail(ActivityEvent activity, int? laterRevision, Localizer localizer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(localizer);
        return activity.Kind switch
        {
            ActivityEventKind.GenerationPaused => activity.Until is { } until
                ? localizer.Format(nameof(Strings.Activity_Detail_GenerationPausedAt), ViewModels.GamePage.GenerationTexts.ClockTime(until, time ?? TimeProvider.System, localizer))
                : localizer[nameof(Strings.Activity_Detail_GenerationPaused)],
            ActivityEventKind.PackIncompatible => laterRevision is { } revision
                ? localizer.Format(nameof(Strings.Activity_Detail_UpdateAvailable), revision)
                : localizer[nameof(Strings.Activity_Detail_Translate)],
            ActivityEventKind.PackNotApplied => localizer[nameof(Strings.Activity_Detail_NotApplied)],
            _ when activity.Detail is { } detail => activity.FilesRestored ? detail : detail + " " + localizer[nameof(Strings.Activity_Detail_NotRestored)],
            _ => null,
        };
    }

    /// <summary>The tone in words (screen readers, tooltips): tone is never color only.</summary>
    public static string Tone(ActivityTone tone, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[tone switch
        {
            ActivityTone.Attention => nameof(Strings.Activity_Tone_Attention),
            ActivityTone.Bad => nameof(Strings.Activity_Tone_Bad),
            ActivityTone.Ok => nameof(Strings.Activity_Tone_Ok),
            ActivityTone.Paused => nameof(Strings.Activity_Tone_Paused),
            _ => nameof(Strings.Activity_Tone_Info),
        }];
    }

    public static string ToneIcon(ActivityTone tone) => tone switch
    {
        ActivityTone.Attention => "IconAlert",
        ActivityTone.Bad => "IconXCircle",
        ActivityTone.Ok => "IconCheckCircle",
        ActivityTone.Paused => "IconPause",
        _ => "IconInfo",
    };

    /// <summary>A day's heading: "Today", "Yesterday", else the date ("Monday, November 3").</summary>
    public static string Day(ActivityDay day, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(localizer);
        return day.Kind switch
        {
            ActivityDayKind.Today => localizer[nameof(Strings.Activity_Day_Today)],
            ActivityDayKind.Yesterday => localizer[nameof(Strings.Activity_Day_Yesterday)],
            _ => day.Date.ToString("dddd, " + localizer.Culture.DateTimeFormat.MonthDayPattern, localizer.Culture),
        };
    }

    /// <summary>An event's time of day in the UI culture ("14:05", "2:05 PM").</summary>
    public static string Time(DateTimeOffset time, TimeZoneInfo zone, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(localizer);
        return TimeZoneInfo.ConvertTime(time, zone).ToString("t", localizer.Culture);
    }

    /// <summary>The 🔔 button's screen reader name.</summary>
    public static string BellName(ActivityBadge badge, bool checking, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(badge);
        ArgumentNullException.ThrowIfNull(localizer);
        if (checking)
        {
            return localizer[nameof(Strings.Chrome_Activity_Checking)];
        }

        return badge.Tone switch
        {
            ActivityBadgeTone.Attention => localizer.Plural(nameof(Strings.Chrome_Activity_Decisions_One), badge.Count),
            ActivityBadgeTone.Normal => localizer.Format(nameof(Strings.Chrome_Activity_Count), badge.Count),
            _ => localizer[nameof(Strings.Chrome_Activity)],
        };
    }
}
