using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;

namespace AiLocalPack.Core.Registry;

/// <summary>
/// The "Think this is a mistake? Report" link of a blocked game (design §3a): a new issue in the registry repo with the
/// title and body filled in, opened in the browser. The text is English (the registry's maintainers read it) and holds
/// what a maintainer needs to lift or confirm the block: game name and ID, store, the reason, the detected file.
/// </summary>
public static class BlockReport
{
    /// <summary>Where registry issues are opened (the organization never moves, like the index URL).</summary>
    public static Uri NewIssueUri { get; } = new("https://github.com/ailocalpack/registry/issues/new");

    /// <summary>"Block report: Some Shooter (steam:123)".</summary>
    public static string Title(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return $"Block report: {game.Name} ({game.Id})";
    }

    /// <param name="detectedFile">The anti-cheat file or folder found (relative, <c>EasyAntiCheat/</c>); <c>null</c> = none, the policy lists the game.</param>
    /// <param name="appVersion">The app's version ("0.4.0").</param>
    public static string Body(Game game, GameBlock block, string? detectedFile, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        var text = new StringBuilder()
            .Append("**Game:** ").Append(game.Name).Append('\n')
            .Append("**Game ID:** `").Append(game.Id).Append("`\n")
            .Append("**Store:** ").Append(game.Source == GameSource.Steam ? "Steam" : "added manually").Append('\n');
        if (block.Reason == LocalizationBlock.OptOut)
        {
            text.Append("**Blocked because:** the registry's opt-out flag (the studio asked not to distribute fan translations)\n");
        }
        else
        {
            text.Append("**Blocked because:** anti-cheat ").Append(block.AntiCheatName ?? block.AntiCheatId).Append(" (`")
                .Append(block.AntiCheatId).Append("`)\n")
                .Append("**Detected file:** ")
                .Append(detectedFile is null ? "none, the game is listed in the anti-cheat policy" : $"`{detectedFile}`")
                .Append('\n');
        }

        return text
            .Append("**App version:** ").Append(appVersion).Append("\n\n")
            .Append("**Why I think this is a mistake** (a link to the studio's statement that mods are allowed helps):\n\n")
            .ToString();
    }

    /// <summary>The new-issue URL with <see cref="Title"/> and <see cref="Body"/> in its query, percent-encoded (UTF-8).</summary>
    /// <inheritdoc cref="Body" path="/param"/>
    public static Uri IssueUri(Game game, GameBlock block, string? detectedFile, string appVersion) => new(
        $"{NewIssueUri}?title={Uri.EscapeDataString(Title(game))}&body={Uri.EscapeDataString(Body(game, block, detectedFile, appVersion))}");
}
