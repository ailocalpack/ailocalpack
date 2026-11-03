using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Steam;

/// <summary>
/// Lists installed Steam games: library folders from <c>steamapps/libraryfolders.vdf</c>, one game per fully installed
/// <c>appmanifest_*.acf</c>, last played from <c>userdata/*/config/localconfig.vdf</c>. Best effort throughout: an unreadable
/// or broken file is logged and skipped, a missing library is reported as not available; a scan never throws for disk state.
/// </summary>
public sealed class SteamLibraryScanner(SteamLocator locator, ILogger<SteamLibraryScanner>? logger = null)
{
    /// <summary><c>StateFlags</c> bit set once every file is on disk (also kept while an update is pending or running).</summary>
    private const uint StateFullyInstalled = 4;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly ILogger _logger = logger ?? NullLogger<SteamLibraryScanner>.Instance;

    /// <summary>Scans every Steam root <see cref="SteamLocator"/> finds; no Steam gives <see cref="SteamScan.Empty"/>.</summary>
    public SteamScan Scan() => Scan(locator.FindRoots());

    /// <summary>Scans every Steam root <see cref="SteamLocator"/> finds, with the player's added and removed library folders.</summary>
    public SteamScan Scan(SteamLibraryChoices choices) => Scan(locator.FindRoots(), choices);

    /// <summary>
    /// Scans the given Steam roots (folders holding <c>steamapps/</c>), then the folders the player added (each one a single
    /// library). Libraries in <see cref="SteamLibraryChoices.Ignored"/> are left out, except the roots themselves. A game
    /// in several libraries is listed once (first one wins); a folder reached twice is listed once, as Steam's.
    /// </summary>
    public SteamScan Scan(IEnumerable<string> roots, SteamLibraryChoices? choices = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        choices ??= SteamLibraryChoices.None;
        var ignored = choices.Ignored.Select(Normalize).ToHashSet(PathComparer);
        var libraries = new List<SteamLibrary>();
        var games = new Dictionary<uint, SteamGame>();
        var seenLibraries = new HashSet<string>(PathComparer);
        var anyLastPlayed = new Dictionary<uint, DateTimeOffset>();
        foreach (var root in roots)
        {
            var lastPlayed = ReadLastPlayed(root);
            foreach (var (appId, played) in lastPlayed)
            {
                if (!anyLastPlayed.TryGetValue(appId, out var known) || played > known)
                {
                    anyLastPlayed[appId] = played;
                }
            }

            var paths = LibraryPaths(root);
            for (var i = 0; i < paths.Count; i++)
            {
                // The root (index 0) is where Steam is: it can't be removed from the list.
                if (i == 0 || !ignored.Contains(paths[i]))
                {
                    AddLibrary(paths[i], addedByHand: false, lastPlayed);
                }
            }
        }

        // Added folders have no Steam of their own: last played comes from any Steam found.
        foreach (var added in choices.Added)
        {
            if (!string.IsNullOrWhiteSpace(added) && Path.IsPathRooted(added))
            {
                AddLibrary(Normalize(added), addedByHand: true, anyLastPlayed);
            }
        }

        void AddLibrary(string path, bool addedByHand, IReadOnlyDictionary<uint, DateTimeOffset> lastPlayed)
        {
            if (!seenLibraries.Add(Identity(path)))
            {
                return;
            }

            var available = Directory.Exists(Path.Combine(path, "steamapps"));
            libraries.Add(new SteamLibrary(path, available, addedByHand));
            if (!available)
            {
                _logger.LogInformation("Steam library {Path} is not available", path);
                return;
            }

            foreach (var game in ReadGames(path, lastPlayed))
            {
                games.TryAdd(game.AppId, game);
            }
        }

        var sorted = games.Values
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.AppId)
            .ToList();
        return new SteamScan(libraries, sorted);
    }

    /// <summary>
    /// The library folder for a folder the player picked: the folder itself when it holds <c>steamapps/</c>, its parent
    /// for a picked <c>steamapps</c>, the grandparent for <c>steamapps/common</c>; <c>null</c> when it isn't a Steam library.
    /// </summary>
    public static string? FindLibraryFolder(string picked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picked);
        try
        {
            var folder = new DirectoryInfo(Normalize(picked));
            if (folder.Name.Equals("common", StringComparison.OrdinalIgnoreCase)
                && folder.Parent is { } parent
                && parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                folder = parent;
            }

            if (folder.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) && folder.Parent is { } library && folder.Exists)
            {
                return Normalize(library.FullName);
            }

            return Directory.Exists(Path.Combine(folder.FullName, "steamapps")) ? Normalize(folder.FullName) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The root itself (always a library), then the folders listed in <c>libraryfolders.vdf</c>.</summary>
    private List<string> LibraryPaths(string root)
    {
        var paths = new List<string> { Normalize(root) };
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file))
        {
            return paths;
        }

        try
        {
            var document = VdfParser.ParseFile(file);
            var folders = document.GetObject("libraryfolders") ?? new VdfObject([]);
            foreach (var (key, value) in folders)
            {
                // Current format: "0" { "path" "..." }. Before 2021: "1" "D:\\SteamLibrary" next to non-numeric keys.
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var path = value switch
                {
                    VdfObject folder => folder.GetString("path"),
                    VdfString text => text.Value,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path))
                {
                    paths.Add(Normalize(path));
                }
            }
        }
        catch (Exception e) when (e is VdfParseException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Cannot read Steam library list {File}; scanning only {Root}", file, root);
        }

        return paths;
    }

    private List<SteamGame> ReadGames(string library, IReadOnlyDictionary<uint, DateTimeOffset> lastPlayed)
    {
        var games = new List<SteamGame>();
        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.GetFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Cannot list Steam library {Path}", library);
            return games;
        }

        foreach (var manifest in manifests.Order(StringComparer.Ordinal))
        {
            if (ReadGame(library, manifest, lastPlayed) is { } game)
            {
                games.Add(game);
            }
        }

        return games;
    }

    private SteamGame? ReadGame(string library, string manifestFile, IReadOnlyDictionary<uint, DateTimeOffset> lastPlayed)
    {
        VdfObject state;
        try
        {
            state = VdfParser.ParseFile(manifestFile).GetObject("AppState")
                ?? throw new VdfParseException("no \"AppState\" block", 1, 1);
        }
        catch (Exception e) when (e is VdfParseException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Skipping unreadable Steam app manifest {File}", manifestFile);
            return null;
        }

        if (!TryParseUInt(state.GetString("appid"), out var appId))
        {
            _logger.LogWarning("Skipping Steam app manifest {File}: no valid appid", manifestFile);
            return null;
        }

        var name = state.GetString("name") is { Length: > 0 } n ? n : $"App {appId}";
        if (SteamTools.IsTool(appId, name))
        {
            return null;
        }

        if (!TryParseUInt(state.GetString("StateFlags"), out var flags) || (flags & StateFullyInstalled) == 0)
        {
            _logger.LogDebug("Skipping {Name} ({AppId}): not fully installed", name, appId);
            return null;
        }

        var installDir = state.GetString("installdir");
        if (string.IsNullOrWhiteSpace(installDir) || installDir.IndexOfAny(['/', '\\']) >= 0 || installDir is "." or "..")
        {
            _logger.LogWarning("Skipping {Name} ({AppId}): invalid installdir {InstallDir}", name, appId, installDir);
            return null;
        }

        var buildId = state.GetString("buildid") is { Length: > 0 } b && b != "0" ? b : null;
        var played = lastPlayed.TryGetValue(appId, out var fromUser) ? fromUser : (DateTimeOffset?)null;
        if (FromUnixSeconds(state.GetString("LastPlayed")) is { } fromManifest && (played is null || fromManifest > played))
        {
            played = fromManifest;
        }

        return new SteamGame(appId, name, buildId, Path.Combine(library, "steamapps", "common", installDir), library, played);
    }

    /// <summary>App ID → latest launch across every Steam user that signed in on this machine.</summary>
    private Dictionary<uint, DateTimeOffset> ReadLastPlayed(string root)
    {
        var result = new Dictionary<uint, DateTimeOffset>();
        string[] users;
        try
        {
            var userdata = Path.Combine(root, "userdata");
            users = Directory.Exists(userdata) ? Directory.GetDirectories(userdata) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Cannot list Steam users in {Root}", root);
            return result;
        }

        foreach (var user in users)
        {
            var file = Path.Combine(user, "config", "localconfig.vdf");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var apps = VdfParser.ParseFile(file)
                    .GetObject("UserLocalConfigStore")?
                    .GetObject("Software")?
                    .GetObject("Valve")?
                    .GetObject("Steam")?
                    .GetObject("apps");
                foreach (var (key, value) in apps ?? new VdfObject([]))
                {
                    if (TryParseUInt(key, out var appId)
                        && value is VdfObject app
                        && FromUnixSeconds(app.GetString("LastPlayed")) is { } played
                        && (!result.TryGetValue(appId, out var known) || played > known))
                    {
                        result[appId] = played;
                    }
                }
            }
            catch (Exception e) when (e is VdfParseException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "Cannot read last played times from {File}", file);
            }
        }

        return result;
    }

    private static DateTimeOffset? FromUnixSeconds(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
        && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static bool TryParseUInt(string? value, out uint result) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>Symlink-free path for spotting one library listed twice (Linux roots often reach the same folder).</summary>
    private static string Identity(string path)
    {
        try
        {
            return SteamLocator.RealPath(path, depth: 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }
}
