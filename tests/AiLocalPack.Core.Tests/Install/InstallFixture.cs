using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>A game folder, a staging folder with new files and an app home, for install transaction tests.</summary>
internal sealed class InstallFixture : IDisposable
{
    public static readonly GameId Id = GameId.Steam(620);

    public InstallFixture()
    {
        Paths = AppPaths.UnderHome(Home.Path);
        GameFolder
            .File("data/Map001.json", "map 1 original")
            .File("data/Map002.json", "map 2 original")
            .File("data/System.json", "system original")
            .File("js/plugins.js", "plugins original")
            .File("Game.exe", "MZ game");
        Game = MakeGame();
    }

    public TempDir Home { get; } = new();

    public TempDir GameFolder { get; } = new();

    public TempDir Staging { get; } = new();

    public AppPaths Paths { get; }

    public FakeProcesses Processes { get; } = new();

    public Game Game { get; private set; }

    public Game MakeGame(string? antiCheat = "none", bool available = true) =>
        Game = new Game(Id, "Sample", GameSource.Steam, GameFolder.Path, null, GameVersion.FromBuildId("1"), "rpgmaker:mz", antiCheat, null, available);

    public PackInstaller Installer(InstalledPackStore? store = null) =>
        new(Paths, store ?? new InstalledPackStore(Paths), AntiCheatPolicy.Bundled, Processes, OsEnvironment.Current);

    /// <summary>Stages the new files and makes the request (pack <c>full</c>, Russian, rev 1).</summary>
    public InstallRequest Request(params (string Path, string Content)[] files) => Request(1, files);

    public InstallRequest Request(int revision, params (string Path, string Content)[] files)
    {
        var folder = $"r{revision}-{Guid.NewGuid():N}";
        foreach (var (path, content) in files)
        {
            Staging.File($"{folder}/{path}", content);
        }

        return new InstallRequest(Game, "ru", "full", revision, [.. files.Select(f => new InstallFile(f.Path, Staging.Combine($"{folder}/{f.Path}")))]);
    }

    /// <summary>The request of pack <paramref name="packId"/> (Russian, rev 1) with whitelisted dependencies.</summary>
    public InstallRequest PackWith(string packId, IReadOnlyList<InstallDependency> dependencies, params (string Path, string Content)[] files) =>
        Request(files) with { PackId = packId, Dependencies = dependencies };

    /// <summary>A dependency's unpacked files, staged.</summary>
    public InstallDependency Dependency(string id, string version, IReadOnlyList<string>? generated = null, params (string Path, string Content)[] files)
    {
        var folder = $"{id}-{version}-{Guid.NewGuid():N}";
        foreach (var (path, content) in files)
        {
            Staging.File($"{folder}/{path}", content);
        }

        return new InstallDependency(id, version, [.. files.Select(f => new InstallFile(f.Path, Staging.Combine($"{folder}/{f.Path}")))])
        {
            GeneratedFiles = generated ?? [],
        };
    }

    /// <summary>BepInEx-like: a loader in the game folder and its core folder.</summary>
    public InstallDependency Loader(string version = "5.4.23.2", IReadOnlyList<string>? generated = null) => Dependency(
        "bepinex",
        version,
        generated,
        ("winhttp.dll", $"loader {version}"),
        ("BepInEx/core/BepInEx.dll", $"core {version}"));

    /// <summary>Five files: three overwritten, two added (one in a new folder).</summary>
    public InstallRequest FiveFiles() => Request(
        ("data/Map001.json", "map 1 ru"),
        ("data/Map002.json", "map 2 ru"),
        ("data/System.json", "system ru"),
        ("data/Fonts.json", "fonts ru"),
        ("fonts/new/font.json", "font ru"));

    public string Read(string path) => File.ReadAllText(GameFolder.Combine(path));

    public bool Exists(string path) => File.Exists(GameFolder.Combine(path)) || Directory.Exists(GameFolder.Combine(path));

    /// <summary>Every file of the game folder with its content, to compare before and after.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(GameFolder.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(GameFolder.Path, p).Replace('\\', '/'),
                p => File.Exists(p) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))) : "<folder>");

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void Dispose()
    {
        Home.Dispose();
        GameFolder.Dispose();
        Staging.Dispose();
    }
}

internal sealed class FakeProcesses : IProcessList
{
    public List<RunningProcess> Running { get; } = [];

    public IReadOnlyList<RunningProcess> Snapshot() => [.. Running];
}
