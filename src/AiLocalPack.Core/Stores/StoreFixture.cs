using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AiLocalPack.Core.Stores;

/// <summary>
/// Writes synthetic store records under a <see cref="StoreLocations.RootVariable"/> folder (task 83; tests and
/// <c>dev fixture stores</c>): Epic Games Launcher manifests, Heroic's Legendary and GOG lists, GOG's registry entries and
/// info files, GOG app bundles, the EA app's uninstall entries and <c>installerdata.xml</c>. Games go to
/// <c>&lt;root&gt;/games/&lt;store&gt;/&lt;name&gt;</c> with a placeholder executable. Never real game files.
/// </summary>
public sealed class StoreFixture
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly JsonObject _registry = [];

    public StoreFixture(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        WriteRegistry();
    }

    public string Root { get; }

    /// <summary>An Epic Games Launcher manifest; returns the game folder (created unless <paramref name="createFolder"/> is false).</summary>
    public string EpicGame(
        string appName,
        string name,
        string? version = "1.0.0-CL-100-Windows",
        string? mainGameAppName = null,
        bool incomplete = false,
        string[]? categories = null,
        bool createFolder = true,
        string? installPath = null)
    {
        var folder = installPath ?? Path.Combine(Root, "games", "Epic", name);
        var item = new JsonObject
        {
            ["FormatVersion"] = 0,
            ["AppName"] = appName,
            ["DisplayName"] = name,
            ["InstallLocation"] = folder,
            ["LaunchExecutable"] = "Game.exe",
            ["AppVersionString"] = version,
            ["MainGameAppName"] = mainGameAppName ?? appName,
            ["bIsIncompleteInstall"] = incomplete,
            ["AppCategories"] = new JsonArray([.. (categories ?? ["public", "games", "applications"]).Select(c => (JsonNode)c)]),
        };
        Write(Path.Combine(Root, "epic-launcher", "Manifests", $"{appName}.item"), item.ToJsonString(Indented));
        return createFolder ? Game(folder) : folder;
    }

    /// <summary>An Epic game in Heroic's Legendary list (or standalone Legendary's with <paramref name="standalone"/>).</summary>
    public string HeroicEpicGame(string appName, string name, string? version = "2.0", bool dlc = false, bool standalone = false)
    {
        var folder = Path.Combine(Root, "games", "Heroic", name);
        var file = standalone
            ? Path.Combine(Root, "legendary", "installed.json")
            : Path.Combine(Root, "heroic", "legendaryConfig", "legendary", "installed.json");
        var list = Read(file) as JsonObject ?? [];
        list[appName] = new JsonObject
        {
            ["app_name"] = appName,
            ["title"] = name,
            ["install_path"] = folder,
            ["executable"] = "Binaries/Game.exe",
            ["version"] = version,
            ["is_dlc"] = dlc,
            ["platform"] = "Windows",
        };
        Write(file, list.ToJsonString(Indented));
        return Game(folder, "Binaries/Game.exe");
    }

    /// <summary>A GOG game in the registry (GOG Galaxy / offline installer) with its <c>goggame-&lt;id&gt;.info</c>.</summary>
    public string GogGame(string productId, string name, string buildId = "56789", string? dependsOn = null, bool createFolder = true)
    {
        var folder = Path.Combine(Root, "games", "GOG", name);
        var values = new JsonObject
        {
            ["gameID"] = productId,
            ["gameName"] = name,
            ["path"] = folder,
            ["buildId"] = buildId,
            ["exe"] = Path.Combine(folder, "Game.exe"),
        };
        if (dependsOn is not null)
        {
            values["dependsOn"] = dependsOn;
        }

        _registry[$@"HKLM32\Software\GOG.com\Games\{productId}"] = values;
        WriteRegistry();
        if (!createFolder)
        {
            return folder;
        }

        Game(folder);
        GogInfo(folder, productId, name, buildId, dependsOn ?? productId);
        return folder;
    }

    /// <summary>A GOG game in Heroic's list, with its info file.</summary>
    public string HeroicGogGame(string productId, string name, string buildId = "777", bool dlc = false)
    {
        var folder = Path.Combine(Root, "games", "Heroic GOG", name);
        var file = Path.Combine(Root, "heroic", "gog_store", "installed.json");
        var root = Read(file) as JsonObject ?? new JsonObject { ["installed"] = new JsonArray() };
        root["installed"]!.AsArray().Add(new JsonObject
        {
            ["appName"] = productId,
            ["install_path"] = folder,
            ["executable"] = "",
            ["version"] = "1.2",
            ["buildId"] = buildId,
            ["is_dlc"] = dlc,
            ["platform"] = "windows",
        });
        Write(file, root.ToJsonString(Indented));
        Game(folder);
        GogInfo(folder, productId, name, buildId, productId);
        return folder;
    }

    /// <summary>A macOS app bundle GOG Galaxy installed (<c>Applications/&lt;name&gt;.app</c>).</summary>
    public string GogBundle(string productId, string name)
    {
        var bundle = Path.Combine(Root, "Applications", $"{name}.app");
        var resources = Path.Combine(bundle, "Contents", "Resources");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", name), "#!/bin/sh\n");
        GogInfo(resources, productId, name, "4242", productId);
        return bundle;
    }

    /// <summary>
    /// An EA app game: its uninstall entry and <c>__Installer/installerdata.xml</c> (<paramref name="utf16"/>: UTF-16, as
    /// some games write it). <paramref name="createFolder"/> false = a drive that isn't there.
    /// </summary>
    public string EaGame(string contentId, string name, string version = "1.0.2.3", bool utf16 = false, bool createFolder = true)
    {
        var folder = Path.Combine(Root, "games", "EA Games", name);
        _registry[$@"HKLM32\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{{{contentId}}}"] = new JsonObject
        {
            ["DisplayName"] = name,
            ["InstallLocation"] = folder,
            ["UninstallString"] = $@"""C:\Program Files\Common Files\EAInstaller\{name}\Cleanup.exe"" uninstall_game -autologging",
        };
        WriteRegistry();
        if (!createFolder)
        {
            return folder;
        }

        Game(folder, "Game.exe");
        var xml = new XDocument(new XElement("DiPManifest", new XAttribute("version", "4.0"),
            new XElement("buildMetaData", new XElement("gameVersion", new XAttribute("version", version))),
            new XElement("contentIDs", new XElement("contentID", contentId)),
            new XElement("gameTitles", new XElement("gameTitle", new XAttribute("locale", "en_US"), name)),
            new XElement("runtime", new XElement("launcher", new XAttribute("uid", "1-1"),
                new XElement("filePath", $@"[HKEY_LOCAL_MACHINE\SOFTWARE\EA Games\{name}\Install Dir]Game.exe")))));
        var path = Path.Combine(folder, "__Installer", "installerdata.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new StreamWriter(path, false, utf16 ? new System.Text.UnicodeEncoding(false, true) : new System.Text.UTF8Encoding(false)))
        {
            xml.Save(writer);
        }

        return folder;
    }

    /// <summary>An uninstall entry that isn't the EA app's (another program).</summary>
    public void OtherProgram(string name)
    {
        _registry[$@"HKLM64\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}"] = new JsonObject
        {
            ["DisplayName"] = name,
            ["InstallLocation"] = Path.Combine(Root, "games", "Other", name),
            ["UninstallString"] = $@"C:\Program Files\{name}\uninstall.exe",
        };
        WriteRegistry();
    }

    /// <summary>A sample of each store for manual checks (<c>dev fixture stores</c>).</summary>
    public void Sample()
    {
        EpicGame("Fennel", "Lanternkeepers");
        EpicGame("Fennel_DLC1", "Lanternkeepers: Night Market", mainGameAppName: "Fennel");
        EpicGame("Marigold", "Harbor of Echoes", version: "++Marigold+Release-2.4-CL-31337-Windows");
        HeroicEpicGame("Juniper", "Clockwork Orchard");
        GogGame("1207658924", "Saltmarsh Tales");
        GogGame("1207658925", "Saltmarsh Tales: Soundtrack", dependsOn: "1207658924");
        HeroicGogGame("1453375253", "Paper Lighthouse");
        GogBundle("1207666666", "Moss and Copper");
        EaGame("1035052", "Starfall Rally");
        EaGame("Origin.OFR.50.0004321", "Ironbark Legends", utf16: true);
        OtherProgram("Some Other Program");
    }

    private void GogInfo(string folder, string productId, string name, string buildId, string rootGameId)
    {
        var info = new JsonObject
        {
            ["gameId"] = productId,
            ["rootGameId"] = rootGameId,
            ["name"] = name,
            ["buildId"] = buildId,
            ["playTasks"] = new JsonArray(new JsonObject { ["isPrimary"] = true, ["path"] = "Game.exe", ["type"] = "FileTask" }),
        };
        Write(Path.Combine(folder, $"goggame-{productId}.info"), info.ToJsonString(Indented));
    }

    private static string Game(string folder, string executable = "Game.exe")
    {
        var exe = Path.Combine(folder, executable.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        if (!File.Exists(exe))
        {
            File.WriteAllBytes(exe, "MZ"u8.ToArray());
        }

        return folder;
    }

    private void WriteRegistry() => Write(Path.Combine(Root, "registry.json"), _registry.ToJsonString(Indented));

    private static JsonNode? Read(string file) => File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null;

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
