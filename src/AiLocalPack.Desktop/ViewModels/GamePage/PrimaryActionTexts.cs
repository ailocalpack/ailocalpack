using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>The words of the game page's primary action: the button label and the caption under it (design §3).</summary>
public static class PrimaryActionTexts
{
    /// <summary>"Install best pack", "Generate translation with AI", "Play", "Update to rev 4", "Translate new strings", "Re-apply", "View progress", "Resume", "Retry stage", "Review glossary"; empty for none.</summary>
    public static string Label(GamePrimaryAction action, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(localizer);
        return action.Kind switch
        {
            GamePrimaryActionKind.InstallBestPack => localizer[nameof(Strings.Game_Action_Install)],
            GamePrimaryActionKind.Generate => localizer[nameof(Strings.Game_Action_Generate)],
            GamePrimaryActionKind.Play => localizer[nameof(Strings.Game_Action_Play)],
            GamePrimaryActionKind.UpdatePack => localizer.Format(nameof(Strings.Game_Action_Update), action.Pack?.Revision ?? 0),
            GamePrimaryActionKind.TranslateNewStrings => localizer[nameof(Strings.Game_Action_TranslateNew)],
            GamePrimaryActionKind.Reapply => localizer[nameof(Strings.Game_Action_Reapply)],
            GamePrimaryActionKind.ViewProgress => localizer[nameof(Strings.Game_Action_ViewProgress)],
            GamePrimaryActionKind.ResumeGeneration => localizer[nameof(Strings.Game_Action_Resume)],
            GamePrimaryActionKind.RetryStage => localizer[nameof(Strings.Game_Action_RetryStage)],
            GamePrimaryActionKind.ReviewGlossary => localizer[nameof(Strings.Game_Action_ReviewGlossary)],
            _ => string.Empty,
        };
    }

    /// <summary>
    /// What exactly happens: "«Фонарщики» · verified · made for your version", "Uses your Claude Code or Codex
    /// subscription", "via Steam · with «…»", "Replaces the installed rev 3", "Generating · 42% · ≈ 25 min left",
    /// "Paused: subscription limit · resumes ~18:00", "Interrupted at 43%", "Stage failed: Build &amp; test"…
    /// </summary>
    /// <param name="time">The clock for a reset time (the player's zone); the system's when not given.</param>
    public static string Caption(GamePrimaryAction action, Game game, Localizer localizer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(localizer);
        time ??= TimeProvider.System;
        return action switch
        {
            { Kind: GamePrimaryActionKind.InstallBestPack, Pack: { } pack } => InstallCaption(pack, game, localizer),
            { Kind: GamePrimaryActionKind.Generate } => localizer[nameof(Strings.Game_Caption_Generate)],
            { Kind: GamePrimaryActionKind.Play } => localizer.Format(
                game.Source == GameSource.Steam ? nameof(Strings.Game_Caption_PlaySteam) : nameof(Strings.Game_Caption_Play),
                action.Pack?.Title ?? action.Installed?.PackId),
            { Kind: GamePrimaryActionKind.UpdatePack, Installed: { } installed } => localizer.Format(nameof(Strings.Game_Caption_Update), installed.Revision),
            { Kind: GamePrimaryActionKind.TranslateNewStrings } => localizer[nameof(Strings.Game_Caption_TranslateNew)],
            { Kind: GamePrimaryActionKind.Reapply, Installed: { } installed } => localizer.Format(
                nameof(Strings.Game_Caption_Reapply), action.Pack?.Title ?? installed.PackId, installed.Revision),
            { Job: { } job } when action.Kind is GamePrimaryActionKind.ViewProgress or GamePrimaryActionKind.ResumeGeneration
                or GamePrimaryActionKind.RetryStage or GamePrimaryActionKind.ReviewGlossary => JobCaption(job, localizer, time),
            _ => string.Empty,
        };
    }

    private static string InstallCaption(RegistryPack pack, Game game, Localizer localizer)
    {
        var parts = new List<string>
        {
            localizer.Format(nameof(Strings.Game_Caption_PackTitle), pack.Title),
            localizer[pack.Status == PackStatus.Verified ? nameof(Strings.Library_Card_Verified) : nameof(Strings.Library_Card_Alpha)],
        };

        // Whether the files match is the Packs tab's check (task 24); the index alone tells whether the pack was made for this build.
        if (game.Version?.BuildId is { } build && pack.BuildIds.Contains(build, StringComparer.Ordinal))
        {
            parts.Add(localizer[nameof(Strings.Game_Caption_ForYourVersion)]);
        }

        return string.Join(" · ", parts);
    }

    private static string JobCaption(GenerationJobState job, Localizer localizer, TimeProvider time)
    {
        var percent = Math.Clamp(job.Percent, 0, 100);
        return job.Phase switch
        {
            GenerationJobPhase.PausedByLimit when job.ResumeAt is { } at =>
                localizer.Format(nameof(Strings.Game_Caption_PausedLimitAt), GenerationTexts.ClockTime(at, time, localizer)),
            GenerationJobPhase.PausedByLimit => localizer.Format(nameof(Strings.Game_Caption_PausedLimit), percent),
            GenerationJobPhase.PausedByPlayer => localizer.Format(nameof(Strings.Game_Caption_Paused), percent),
            GenerationJobPhase.Interrupted => localizer.Format(nameof(Strings.Game_Caption_Interrupted), percent),
            GenerationJobPhase.Failed => localizer.Format(nameof(Strings.Game_Caption_Failed), GenerationTexts.StepName(job.FailedStage ?? Core.Generation.GenerationStage.Build, localizer)),
            GenerationJobPhase.WaitingForReview => localizer[nameof(Strings.Game_Caption_Review)],
            _ when job.Remaining is { } left => localizer.Format(nameof(Strings.Game_Caption_GeneratingEta), percent, GenerationTexts.Duration(left, localizer)),
            _ => localizer.Format(nameof(Strings.Game_Caption_Generating), percent),
        };
    }
}
