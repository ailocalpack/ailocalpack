using System.IO.Enumeration;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Folders;

/// <summary>
/// Finds the games in a folder the player picked: the folder itself, or games in subfolders down to
/// <see cref="FolderScanOptions.MaxDepth"/> (design §2). A folder is a game when it holds an executable (<c>.exe</c>,
/// Linux binary, <c>.sh</c>, <c>.app</c> bundle; installers and helpers don't count), an engine layout, or a binaries
/// subfolder with one (<c>bin/x64/game.exe</c>). The scan never descends into a game, so its launcher, <c>Engine/</c> or
/// <c>EasyAntiCheat/</c> stay part of it. Redistributables, installers, known utilities and save folders are left out
/// with a reason, as is the topmost folder of each branch without games. A Steam library (a folder with
/// <c>steamapps/</c>) is searched in <c>steamapps/common</c> only and its games are flagged. Breadth-first, stops at
/// <see cref="FolderScanOptions.MaxEntries"/>; cancelling returns what was found so far. Best effort: unreadable folders
/// look empty, never an exception.
/// </summary>
public sealed class FolderRecognizer(EngineDetector? engines = null, ILogger<FolderRecognizer>? logger = null)
{
    /// <summary>
    /// Folders a game keeps its executables in, looked into (two levels: <c>bin/x64</c>) when a folder has none. Only names
    /// nobody gives a folder of games: a match makes the parent one game and hides its other subfolders.
    /// </summary>
    private static readonly string[] BinaryFolders = ["bin", "bin32", "bin64", "binaries", "x64", "x86", "win32", "win64"];

    private static readonly (SkipReason Reason, string[] Patterns)[] NamedSkips =
    [
        (SkipReason.Redistributable, [
            "_CommonRedist", "CommonRedist", "Redist", "Redistributable*", "__redist", "__support", "DirectX*", "vcredist*",
            "VC_redist*", "DotNet*", "dotnetfx*", "NetFramework*", "PhysX*", "OpenAL*", "XNA*", "Steamworks Shared",
            "Steamworks Common Redistributables",
        ]),
        (SkipReason.Installer, ["__Installer", "Installer", "Installers", "Setup", "Setups"]),
        (SkipReason.Utility, [
            "Proton *", "Proton - Experimental", "Steam Linux Runtime*", "SteamVR", "Steam Controller Configs", "MSI Afterburner",
            "RivaTuner Statistics Server", "Cheat Engine*", "ReShade*", "Vortex", "Mod Organizer*", "DS4Windows", "SpecialK",
        ]),
        (SkipReason.Saves, ["Save", "Saves", "SaveData", "Save Data", "Savegames", "Save Games", "Saved Games", "My Games"]),
    ];

    /// <summary>Executables shipped next to a game that are rarely the game itself.</summary>
    private static readonly string[] SecondaryNameParts = ["editor", "server", "config", "settings", "benchmark", "tool"];

    /// <summary>System folders not worth listing as skipped.</summary>
    private static readonly string[] Ignored = ["$RECYCLE.BIN", "System Volume Information", "__MACOSX", "lost+found"];

    private readonly EngineDetector _engines = engines ?? new EngineDetector();
    private readonly ILogger _logger = logger ?? NullLogger<FolderRecognizer>.Instance;

    public FolderScan Scan(
        string folder,
        FolderScanOptions? options = null,
        IProgress<FolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        options ??= FolderScanOptions.Default;
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var nodes = new List<Node> { new(FolderView.Root(rootPath), 0, -1) };
        var queue = new Queue<int>([0]);
        var games = new List<FoundGame>();
        var entries = 0;
        var scanned = 0;
        bool limitReached = false, stopped = false;
        while (queue.TryDequeue(out var index))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            var node = nodes[index];
            var view = node.Folder;
            if (index > 0 && NamedSkip(view.Name) is { } reason)
            {
                node.Reason = reason;
                node.Visited = true;
                continue;
            }

            var listed = view.FileNames.Count + view.DirNames.Count;
            if (entries + listed > options.MaxEntries)
            {
                _logger.LogInformation("Folder scan of {Folder} stopped at {Count} entries", rootPath, entries);
                limitReached = true;
                break;
            }

            entries += listed;
            node.Visited = true;
            scanned++;
            FoundGame? found = null;
            if (view.Dir("steamapps") is { } steamApps && !EngineDetector.IsBundle(view.Name))
            {
                // Steam library or Steam itself: only steamapps/common holds games (compatdata holds Windows prefixes).
                Enqueue(nodes, queue, index, [steamApps], options);
            }
            else if (view.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                Enqueue(nodes, queue, index, view.Dir("common") is { } common ? [common] : [], options);
            }
            else if (IsGame(view))
            {
                found = Recognize(view);
                node.IsGame = true;
                games.Add(found);
                for (var parent = node.Parent; parent >= 0 && !nodes[parent].HasGame; parent = nodes[parent].Parent)
                {
                    nodes[parent].HasGame = true;
                }
            }
            else
            {
                node.Reason = Hint(view);
                Enqueue(nodes, queue, index, view.DirNames.Where(n => !IsIgnored(n)).Select(n => view.Dir(n)!), options);
            }

            progress?.Report(new FolderScanProgress(scanned, nodes.Count, games.Count, found));
        }

        var skipped = Skipped(nodes);
        _logger.LogInformation(
            "Folder scan of {Folder}: {Games} games, {Skipped} skipped, {Folders} folders, {Entries} entries",
            rootPath, games.Count, skipped.Count, scanned, entries);
        return new FolderScan(
            rootPath,
            [.. games.OrderBy(g => g.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.RelativePath, StringComparer.Ordinal)],
            skipped,
            limitReached,
            stopped);
    }

    private static void Enqueue(List<Node> nodes, Queue<int> queue, int parent, IEnumerable<FolderView> children, FolderScanOptions options)
    {
        var depth = nodes[parent].Depth + 1;
        if (depth > options.MaxDepth)
        {
            return;
        }

        foreach (var child in children)
        {
            nodes.Add(new Node(child, depth, parent));
            queue.Enqueue(nodes.Count - 1);
        }
    }

    /// <summary>The topmost folder of each game-less branch, with the most telling reason found in it.</summary>
    private static List<SkippedFolder> Skipped(List<Node> nodes)
    {
        // Children come after their parents (breadth-first), so one backward pass carries reasons up.
        var subtree = nodes.Select(n => n.Reason).ToArray();
        for (var i = nodes.Count - 1; i > 0; i--)
        {
            var parent = nodes[i].Parent;
            if (nodes[i].Visited && !nodes[i].HasGame && !nodes[i].IsGame && subtree[i] is { } reason && subtree[parent] is null)
            {
                subtree[parent] = reason;
            }
        }

        return
        [
            .. nodes
                .Select((n, i) => (Node: n, Reason: subtree[i]))
                .Where(x => x.Node.Parent >= 0 && x.Node.Visited && !x.Node.IsGame && !x.Node.HasGame
                    && (x.Node.Parent == 0 || nodes[x.Node.Parent].HasGame))
                .Select(x => new SkippedFolder(x.Node.Folder.Relative, x.Reason ?? SkipReason.NoGame))
                .OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.RelativePath, StringComparer.Ordinal),
        ];
    }

    private FoundGame Recognize(FolderView view)
    {
        var game = FolderView.Root(view.FullPath);
        var engine = _engines.Detect(view.FullPath);
        var name = EngineDetector.IsBundle(game.Name) ? game.Name[..^4] : game.Name;
        var exe = engine.Match?.MainExecutable ?? FirstExecutable(game) ?? BinaryFolderExecutable(game);
        var found = new FoundGame(view.FullPath, view.Relative, name, engine, exe, IsInSteamLibrary(view.FullPath));
        _logger.LogDebug("Game {Name} ({Engine}, {Executable}) in {Folder}", name, engine.Kind, exe, view.FullPath);
        return found;
    }

    /// <summary>
    /// Executables of a game folder a player can choose as the main one (Edit game details): <paramref name="mainExecutable"/>
    /// first, then the folder's executables in the recognizer's order (Windows, Linux, scripts, bundles; a bundle's
    /// <c>Contents/MacOS</c> binaries), then those of binaries subfolders (<c>bin/x64</c>…). Relative, '/'-separated.
    /// Never throws for disk state.
    /// </summary>
    public static IReadOnlyList<string> ListExecutables(string gameFolder, string? mainExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var folder = FolderView.Root(Path.GetFullPath(gameFolder));
        var list = new List<string>();
        if (mainExecutable is not null)
        {
            list.Add(mainExecutable);
        }

        if (IsBundleLike(folder))
        {
            if (folder.At("Contents/MacOS") is { } macOs)
            {
                list.AddRange(macOs.FileNames.Select(macOs.RelativeOf));
            }
        }
        else
        {
            var scripts = folder.FileNames
                .Where(n => n.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) && !Executables.IsInstaller(n) && !Executables.IsHelper(n))
                .Select(folder.RelativeOf);
            var bundles = folder.DirNames.Where(EngineDetector.IsBundle).Select(folder.RelativeOf);
            list.AddRange(Executables.In(folder).Concat(scripts).Concat(bundles));
            AddBinaryFolders(folder, list, 2);
        }

        return [.. list.Distinct(StringComparer.Ordinal)];

        static void AddBinaryFolders(FolderView folder, List<string> list, int levels)
        {
            foreach (var name in folder.DirNames.Where(n => BinaryFolders.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                var bin = folder.Dir(name)!;
                list.AddRange(Executables.In(bin));
                if (levels > 1)
                {
                    AddBinaryFolders(bin, list, levels - 1);
                }
            }
        }
    }

    private static bool IsGame(FolderView folder) =>
        IsBundleLike(folder)
        || FirstExecutable(folder) is not null
        || EngineDetector.HasLayout(folder)
        || BinaryFolderExecutable(folder) is not null;

    /// <summary>A macOS bundle: <c>Game.app</c>, or a folder holding a bare <c>Contents/Info.plist</c> (some Steam Mac installs).</summary>
    private static bool IsBundleLike(FolderView folder) =>
        EngineDetector.IsBundle(folder.Name) || folder.FileAt("Contents/Info.plist") is not null;

    /// <summary>
    /// The folder's main executable: Windows, then Linux executables, then shell scripts, then macOS bundles; for a bundle
    /// itself, its binary. Within those, one named like the folder wins, and editors, servers and settings tools come last.
    /// </summary>
    private static string? FirstExecutable(FolderView folder)
    {
        if (IsBundleLike(folder))
        {
            return folder.At("Contents/MacOS") is { } macOs ? Pick(macOs.FileNames.Select(macOs.RelativeOf), folder.Name) : null;
        }

        var scripts = folder.FileNames
            .Where(n => n.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) && !Executables.IsInstaller(n) && !Executables.IsHelper(n))
            .Select(folder.RelativeOf);
        var bundles = folder.DirNames.Where(EngineDetector.IsBundle).Select(folder.RelativeOf);
        return Pick(Executables.In(folder).Concat(scripts).Concat(bundles), folder.Name);
    }

    private static string? Pick(IEnumerable<string> candidates, string folderName)
    {
        var stem = Normalize(EngineDetector.IsBundle(folderName) ? folderName[..^4] : folderName);
        return candidates
            .Select((path, order) => (Path: path, Order: order, Name: Path.GetFileNameWithoutExtension(path.Split('/')[^1])))
            .OrderBy(c => Normalize(c.Name) == stem ? 0 : SecondaryNameParts.Any(p => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)) ? 2 : 1)
            .ThenBy(c => c.Order)
            .Select(c => c.Path)
            .FirstOrDefault();

        static string Normalize(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
    }

    /// <summary>An executable in a binaries subfolder (<c>bin/</c>, <c>x64/</c>, <c>bin/x64/</c>…).</summary>
    private static string? BinaryFolderExecutable(FolderView folder, int levels = 2)
    {
        foreach (var name in folder.DirNames.Where(n => BinaryFolders.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            var bin = folder.Dir(name)!;
            if (Executables.In(bin).FirstOrDefault() is { } exe)
            {
                return exe;
            }

            if (levels > 1 && BinaryFolderExecutable(bin, levels - 1) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>What a folder without a game looks like from its files: installers or redistributable installers.</summary>
    private static SkipReason? Hint(FolderView folder)
    {
        static bool IsProgram(string n) =>
            n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);

        var programs = folder.FileNames.Where(IsProgram).ToList();
        if (programs.Any(n => n.Contains("redist", StringComparison.OrdinalIgnoreCase)
            || n.Contains("prereq", StringComparison.OrdinalIgnoreCase)
            || n.Contains("dxsetup", StringComparison.OrdinalIgnoreCase)))
        {
            return SkipReason.Redistributable;
        }

        return programs.Any(Executables.IsInstaller) ? SkipReason.Installer : null;
    }

    private static SkipReason? NamedSkip(string name)
    {
        foreach (var (reason, patterns) in NamedSkips)
        {
            if (patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
            {
                return reason;
            }
        }

        return null;
    }

    private static bool IsIgnored(string name) =>
        name.StartsWith('.') || Ignored.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="gamePath"/> is inside a <c>steamapps/common</c> folder (also when the scan started inside it).</summary>
    private static bool IsInSteamLibrary(string gamePath)
    {
        var parts = gamePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 2 < parts.Length; i++)
        {
            if (parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Node(FolderView folder, int depth, int parent)
    {
        public FolderView Folder { get; } = folder;

        public int Depth { get; } = depth;

        public int Parent { get; } = parent;

        public bool Visited { get; set; }

        public bool IsGame { get; set; }

        /// <summary>A game was found below.</summary>
        public bool HasGame { get; set; }

        /// <summary>Named skip, or the hint from the folder's own files.</summary>
        public SkipReason? Reason { get; set; }
    }
}
