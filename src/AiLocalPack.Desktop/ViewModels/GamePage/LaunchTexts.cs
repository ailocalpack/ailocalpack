using AiLocalPack.Core.Launch;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>Why a game didn't start, for this OS (the header's amber line, the install toast's Play).</summary>
public static class LaunchTexts
{
    /// <summary>The plain reason a launch failed; empty when it worked.</summary>
    public static string Problem(GameLaunchResult result, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);
        return result switch
        {
            { Error: { } error } => localizer.Format(nameof(Strings.Game_Launch_Failed), error),
            { Problem: LaunchProblem.GameUnavailable } => localizer[nameof(Strings.Game_Launch_Unavailable)],
            { Problem: LaunchProblem.NoExecutable } => localizer[nameof(Strings.Game_Launch_NoExecutable)],
            { Problem: LaunchProblem.WindowsOnly } => localizer[OperatingSystem.IsLinux() ? nameof(Strings.Game_Launch_WindowsOnlyLinux) : nameof(Strings.Game_Launch_WindowsOnly)],
            { Problem: LaunchProblem.LinuxOnly } => localizer[nameof(Strings.Game_Launch_LinuxOnly)],
            { Problem: LaunchProblem.MacOnly } => localizer[nameof(Strings.Game_Launch_MacOnly)],
            _ => string.Empty,
        };
    }
}
