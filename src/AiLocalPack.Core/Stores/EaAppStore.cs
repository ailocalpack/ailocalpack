using System.Xml;
using System.Xml.Linq;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Stores;

/// <summary>
/// EA app games (formerly Origin; task 86), Windows only: the uninstall entries the EA app writes
/// (<c>SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c> in HKLM and HKCU, both registry views, whose
/// <c>UninstallString</c> runs <c>EAInstaller\…\Cleanup.exe</c>: <c>DisplayName</c>, <c>InstallLocation</c>), then the game
/// folder's <c>__Installer/installerdata.xml</c> (<see cref="EaInstallerData"/>) for the content ID, name, version and
/// launcher. EA's own encrypted list of installs (<c>IS</c>, keyed to the PC's hardware) is not read.
/// </summary>
public sealed class EaAppStore(StoreLocations locations, ILogger<EaAppStore> logger) : IGameStore
{
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public GameSource Source => GameSource.Ea;

    public bool IsSupportedOnThisOs => locations.Os == OsKind.Windows || locations.IsOverridden;

    public StoreScan Scan()
    {
        var games = new Dictionary<GameId, StoreGame>();
        var missing = new List<string>();
        var registry = locations.Registry;
        var found = false;
        foreach (var hive in (RegistryHiveKind[])[RegistryHiveKind.LocalMachine, RegistryHiveKind.CurrentUser])
        {
            foreach (var view32 in (bool[])[true, false])
            {
                foreach (var key in registry.SubKeyNames(hive, UninstallKey, view32))
                {
                    var path = $@"{UninstallKey}\{key}";
                    string? Value(string name) => registry.GetString(hive, path, name, view32);
                    var uninstall = Value("UninstallString") ?? "";
                    if (!uninstall.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase)
                        || !uninstall.Contains("Cleanup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    found = true;
                    if (FromEntry(key, Value("DisplayName"), Value("InstallLocation"), missing) is { } game)
                    {
                        games.TryAdd(game.Id, game);
                    }
                }
            }
        }

        return new StoreScan(Source, found, found ? $@"HKEY_LOCAL_MACHINE\{UninstallKey}" : null, EpicStore.Sorted(games.Values), MissingFolders: missing);
    }

    private StoreGame? FromEntry(string key, string? displayName, string? installLocation, List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            logger.LogWarning("Skipping EA app entry {Key}: no install location", key);
            return null;
        }

        var folder = Path.TrimEndingDirectorySeparator(installLocation.Trim().Trim('"'));
        if (!StoreFiles.Exists(folder))
        {
            // On an unplugged drive: without its installer data there is no content ID to name it by, so the catalog
            // keeps what an earlier scan knew of it, as unavailable.
            missing.Add(folder);
            return null;
        }

        if (EaInstallerData.Read(folder) is not { } data || GameId.Store(GameIdKind.Ea, data.ContentId) is not { } id)
        {
            logger.LogWarning("Skipping EA app game {Folder}: no readable __Installer/installerdata.xml with a content ID", folder);
            return null;
        }

        return new StoreGame(
            id,
            data.Title ?? displayName?.Trim() ?? Path.GetFileName(folder),
            folder,
            StoreFiles.LibraryOf(folder),
            StoreFiles.Version(data.Version),
            data.Launcher,
            StoreApps.EaApp);
    }
}

/// <summary>
/// What an EA game's <c>__Installer/installerdata.xml</c> says (UTF-8 or UTF-16): the first <c>contentIDs/contentID</c>,
/// the title (<c>gameTitles/gameTitle</c>, <c>en_US</c> first; older files <c>metadata/localeInfo/title</c>), the version
/// (<c>buildMetaData/gameVersion@version</c>) and the first <c>runtime/launcher/filePath</c> (a
/// <c>[HKEY_…\Install Dir]Game.exe</c> reference read relative to the game folder). Element names are matched without case.
/// </summary>
public sealed record EaInstallerData(string ContentId, string? Title, string? Version, string? Launcher)
{
    public const string RelativePath = "__Installer/installerdata.xml";

    public static EaInstallerData? Read(string folder)
    {
        XDocument xml;
        try
        {
            using var stream = File.OpenRead(Path.Combine(folder, "__Installer", "installerdata.xml"));
            xml = XDocument.Load(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        var root = xml.Root;
        if (root is null || First(root, "contentIDs", "contentID") is not { } content || string.IsNullOrWhiteSpace(content.Value))
        {
            return null;
        }

        var titles = Children(First(root, "gameTitles"), "gameTitle").ToList();
        var title = titles.FirstOrDefault(t => (string?)t.Attribute("locale") is "en_US") ?? titles.FirstOrDefault()
            ?? First(root, "metadata", "localeInfo", "title");
        var version = First(root, "buildMetaData", "gameVersion")?.Attribute("version")?.Value;
        var launcher = Children(First(root, "runtime"), "launcher")
            .Select(l => Children(l, "filePath").FirstOrDefault()?.Value)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        return new EaInstallerData(content.Value.Trim(), Trimmed(title?.Value), Trimmed(version), LauncherPath(folder, launcher));
    }

    /// <summary><c>[HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Apex\Install Dir]r5apex.exe</c> → <c>r5apex.exe</c>.</summary>
    private static string? LauncherPath(string folder, string? filePath)
    {
        if (filePath is null)
        {
            return null;
        }

        var path = filePath.Trim();
        if (path.StartsWith('[') && path.IndexOf(']', StringComparison.Ordinal) is var end and > 0)
        {
            path = path[(end + 1)..];
        }

        return StoreFiles.Relative(folder, path);
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static XElement? First(XElement? element, params string[] path)
    {
        foreach (var name in path)
        {
            element = element is null ? null : Children(element, name).FirstOrDefault();
        }

        return element;
    }

    private static IEnumerable<XElement> Children(XElement? element, string name) =>
        element?.Elements().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) ?? [];
}
