using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Generation;

/// <summary>
/// The folders outside the game folder where a game's engine keeps what it writes when it runs (settings, caches, logs,
/// saves), for a test run's write guard (<see cref="GameWriteGuard"/>, _Live game folder_ in <c>docs/generation.md</c>):
/// Unity's persistent data under the company and product of <c>app.info</c> (Windows <c>AppData/LocalLow</c>, macOS
/// <c>Library/Application Support</c> and <c>Library/Logs</c>, Linux <c>~/.config/unity3d</c>); Godot's
/// <c>app_userdata/&lt;game name&gt;</c>; a packaged Unreal game's <c>&lt;Project&gt;/Saved</c> (Windows
/// <c>%LOCALAPPDATA%</c>, Linux <c>~/.config/Epic</c>, macOS <c>Library/Application Support/Epic</c>). RPG Maker and
/// Ren'Py write their saves into the game folder itself (Ren'Py's copy under the user's <c>RenPy/</c> folder is named
/// by the game's own setting, not known yet). A folder may not exist yet. Never throws for disk state.
/// </summary>
public static class GameDataFolders
{
    public static IReadOnlyList<GameWriteRoot> For(string gameFolder, GameEngine engine, string gameName, OsEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameFolder);
        ArgumentNullException.ThrowIfNull(gameName);
        ArgumentNullException.ThrowIfNull(environment);
        var home = environment.HomeDirectory;
        var roots = new List<GameWriteRoot>();
        switch (engine)
        {
            case GameEngine.Unity when GameLogs.UnityNames(gameFolder) is var (company, product):
                switch (environment.Os)
                {
                    case OsKind.Windows:
                        roots.Add(new("unity", Path.Combine(home, "AppData", "LocalLow", company, product)));
                        break;
                    case OsKind.MacOS:
                        roots.Add(new("unity", Path.Combine(home, "Library", "Application Support", company, product)));
                        roots.Add(new("unity-logs", Path.Combine(home, "Library", "Logs", company, product)));
                        break;
                    default:
                        roots.Add(new("unity", Path.Combine(home, ".config", "unity3d", company, product)));
                        break;
                }

                break;

            case GameEngine.Godot when IsName(gameName):
                var userData = environment.Os switch
                {
                    OsKind.Windows => Path.Combine(environment.GetVariable("APPDATA") ?? Path.Combine(home, "AppData", "Roaming"), "Godot", "app_userdata"),
                    OsKind.MacOS => Path.Combine(home, "Library", "Application Support", "Godot", "app_userdata"),
                    _ => Path.Combine(environment.GetVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "godot", "app_userdata"),
                };
                roots.Add(new("godot", Path.Combine(userData, gameName)));
                break;

            case GameEngine.Unreal:
                var saved = environment.Os switch
                {
                    OsKind.Windows => environment.GetVariable("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local"),
                    OsKind.MacOS => Path.Combine(home, "Library", "Application Support", "Epic"),
                    _ => Path.Combine(environment.GetVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "Epic"),
                };
                foreach (var project in Projects(gameFolder))
                {
                    roots.Add(new("unreal-" + Slug(project), Path.Combine(saved, project, "Saved")));
                }

                break;
        }

        return roots;
    }

    /// <summary>Unreal projects of a packaged game: folders beside <c>Engine/</c> holding <c>Content/</c>.</summary>
    private static List<string> Projects(string gameFolder)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(gameFolder)
                .Where(d => Directory.Exists(Path.Combine(d, "Content")) && !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(IsName)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsName(string name) => name.Trim().Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    private static string Slug(string name) => new([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
}
