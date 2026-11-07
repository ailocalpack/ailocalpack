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
    /// <summary>"Install best pack", "Generate translation with AI", "Play", "Update to rev 4", "Translate new strings", "Re-apply", "View progress"; empty for none.</summary>
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
            _ => string.Empty,
        };
    }

    /// <summary>
    /// What exactly happens: "«Фонарщики» · verified · made for your version", "Uses your Claude Code or Codex
    /// subscription", "via Steam · with «…»", "Replaces the installed rev 3", "Generating · 42% · ≈ 25 min left"…
    /// </summary>
    public static string Caption(GamePrimaryAction action, Game game, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(localizer);
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
            { Kind: GamePrimaryActionKind.ViewProgress, Job: { } job } => JobCaption(job, localizer),
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

    private static string JobCaption(GenerationJobState job, Localizer localizer)
    {
        var percent = Math.Clamp(job.Percent, 0, 100);
        if (job.IsPaused)
        {
            return job.PauseReason is { } reason
                ? localizer.Format(nameof(Strings.Game_Caption_PausedReason), percent, reason)
                : localizer.Format(nameof(Strings.Game_Caption_Paused), percent);
        }

        return job.Remaining is { } left
            ? localizer.Format(nameof(Strings.Game_Caption_GeneratingEta), percent, Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)))
            : localizer.Format(nameof(Strings.Game_Caption_Generating), percent);
    }
}
