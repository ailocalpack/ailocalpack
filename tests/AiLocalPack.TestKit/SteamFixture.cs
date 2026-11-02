using System.Globalization;
using System.Text;

namespace AiLocalPack.TestKit;

/// <summary>
/// Builds a synthetic Steam install inside a <see cref="TempDir"/>: a root with <c>steamapps/</c>, extra library folders,
/// <c>libraryfolders.vdf</c>, app manifests, game folders and per-user <c>localconfig.vdf</c>. No real game files.
/// </summary>
public sealed class SteamFixture
{
    /// <summary><c>StateFlags</c> of a fully installed app.</summary>
    public const int FullyInstalled = 4;

    /// <summary><c>StateFlags</c> of an app still downloading for the first time (update required, not installed).</summary>
    public const int Downloading = 1026;

    private readonly TempDir _dir;
    private readonly List<string> _libraries = [];

    /// <param name="dir">Folder to build in.</param>
    /// <param name="rootRelativePath">Steam root, relative to <paramref name="dir"/>.</param>
    public SteamFixture(TempDir dir, string rootRelativePath = "Steam")
    {
        ArgumentNullException.ThrowIfNull(dir);
        _dir = dir;
        Root = dir.Dir($"{rootRelativePath}/steamapps").Combine(rootRelativePath);
        _libraries.Add(Root);
    }

    /// <summary>Absolute path of the Steam root.</summary>
    public string Root { get; }

    /// <summary>
    /// Adds a library folder (absolute path) to <c>libraryfolders.vdf</c>. With <paramref name="create"/> = false the folder
    /// is listed but does not exist (unplugged drive).
    /// </summary>
    public string AddLibrary(string relativePath, bool create = true)
    {
        if (create)
        {
            _dir.Dir($"{relativePath}/steamapps");
        }

        var path = _dir.Combine(relativePath);
        _libraries.Add(path);
        WriteLibraryFolders();
        return path;
    }

    /// <summary>Writes <c>appmanifest_&lt;appId&gt;.acf</c> into <paramref name="library"/> (default: the root) and creates the game folder.</summary>
    public SteamFixture AddGame(
        uint appId,
        string name,
        string buildId = "1000",
        int stateFlags = FullyInstalled,
        string? library = null,
        long lastPlayed = 0)
    {
        library ??= Root;
        var installDir = name;
        var manifest = $$"""
            "AppState"
            {
            	"appid"		"{{appId}}"
            	"Universe"		"1"
            	"name"		"{{Escape(name)}}"
            	"StateFlags"		"{{stateFlags}}"
            	"installdir"		"{{Escape(installDir)}}"
            	"LastPlayed"		"{{lastPlayed}}"
            	"buildid"		"{{buildId}}"
            }
            """;
        Write(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"), manifest);
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", installDir));
        return this;
    }

    /// <summary>Writes a raw file into the root's <c>steamapps/</c> (broken manifests and the like).</summary>
    public SteamFixture AddRawSteamappsFile(string fileName, string content)
    {
        Write(Path.Combine(Root, "steamapps", fileName), content);
        return this;
    }

    /// <summary>Writes <c>userdata/&lt;userId&gt;/config/localconfig.vdf</c> with last played times (Unix seconds) per app.</summary>
    public SteamFixture SetLastPlayed(string userId, params (uint AppId, long UnixSeconds)[] apps)
    {
        var text = new StringBuilder();
        text.AppendLine("\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{");
        foreach (var (appId, seconds) in apps)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\t\t\"{appId}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"{seconds}\"\n\t\t\t\t\t}}");
        }

        text.AppendLine("\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}");
        Write(Path.Combine(Root, "userdata", userId, "config", "localconfig.vdf"), text.ToString());
        return this;
    }

    private void WriteLibraryFolders()
    {
        var text = new StringBuilder("\"libraryfolders\"\n{\n");
        for (var i = 0; i < _libraries.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{Escape(_libraries[i])}\"\n\t}}\n");
        }

        text.Append("}\n");
        Write(Path.Combine(Root, "steamapps", "libraryfolders.vdf"), text.ToString());
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
