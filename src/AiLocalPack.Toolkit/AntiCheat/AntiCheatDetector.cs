using System.IO.Enumeration;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.AntiCheat;

/// <summary>One anti-cheat found in a game folder.</summary>
/// <param name="Id">Anti-cheat ID (<see cref="AntiCheatRule.Id"/>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Marker">
/// The matched file or folder, relative to the game folder with '/' separators; folders end with '/'
/// (<c>EasyAntiCheat/</c>).
/// </param>
public sealed record AntiCheatMatch(string Id, string Name, string Marker);

/// <summary>Result of <see cref="AntiCheatDetector.Detect"/>.</summary>
/// <param name="Matches">Every anti-cheat found, one per anti-cheat, nearest the top first (then in rule order).</param>
public sealed record AntiCheatDetection(IReadOnlyList<AntiCheatMatch> Matches)
{
    public static AntiCheatDetection None { get; } = new([]);

    /// <summary>The main finding, or <c>null</c> when the folder has no anti-cheat files.</summary>
    public AntiCheatMatch? Match => Matches.Count > 0 ? Matches[0] : null;

    /// <summary>Value stored in <see cref="Game.AntiCheat"/>: the anti-cheat ID or <see cref="AntiCheatPolicy.NoneId"/>.</summary>
    public string StoredId => Match?.Id ?? AntiCheatPolicy.NoneId;
}

/// <summary>
/// Finds anti-cheat files in a game folder with the marker rules of an <see cref="AntiCheatPolicy"/> (default: the bundled
/// list). Walks the folder breadth-first down to <see cref="MaxDepth"/> levels (anti-cheats sit next to the executable,
/// which can be nested like <c>Project/Binaries/Win64</c> or inside a macOS bundle) and stops after
/// <see cref="MaxFolders"/> folders. Names ignore case. Best effort: unreadable or missing folders give no match, never an
/// exception. Whether a match blocks the game is the policy's call (<see cref="AntiCheatPolicy.Resolve(GameId, string?)"/>).
/// </summary>
public sealed class AntiCheatDetector
{
    /// <summary>Deepest folder level searched (the game folder is 0).</summary>
    public const int MaxDepth = 6;

    /// <summary>Folders searched at most per game, so a huge asset tree can't stall a scan.</summary>
    public const int MaxFolders = 20_000;

    private readonly AntiCheatPolicy _policy;
    private readonly ILogger _logger;
    private readonly (AntiCheatRule Rule, Marker[] Markers)[] _rules;

    public AntiCheatDetector(AntiCheatPolicy? policy = null, ILogger<AntiCheatDetector>? logger = null)
    {
        _policy = policy ?? AntiCheatPolicy.Bundled;
        _logger = logger ?? NullLogger<AntiCheatDetector>.Instance;
        _rules = [.. _policy.AntiCheats.Select(r => (r, r.Markers.Select(Marker.Parse).ToArray()))];
    }

    public AntiCheatDetection Detect(string gameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var found = new List<AntiCheatMatch>();
        var queue = new Queue<(FolderView Folder, int Depth)>();
        queue.Enqueue((FolderView.Root(Path.GetFullPath(gameFolder)), 0));
        var visited = 0;
        while (queue.TryDequeue(out var item) && found.Count < _rules.Length)
        {
            if (++visited > MaxFolders)
            {
                _logger.LogInformation("Anti-cheat search stopped after {Count} folders in {Folder}", MaxFolders, gameFolder);
                break;
            }

            var (folder, depth) = item;
            foreach (var (rule, markers) in _rules)
            {
                if (found.Any(m => m.Id == rule.Id))
                {
                    continue;
                }

                var marker = markers.Select(m => m.Find(folder)).FirstOrDefault(m => m is not null);
                if (marker is not null)
                {
                    found.Add(new AntiCheatMatch(rule.Id, rule.Name, marker));
                }
            }

            if (depth < MaxDepth)
            {
                foreach (var name in folder.DirNames)
                {
                    queue.Enqueue((folder.Dir(name)!, depth + 1));
                }
            }
        }

        if (found.Count > 0)
        {
            _logger.LogInformation(
                "Anti-cheat in {Folder}: {Matches}", gameFolder, string.Join(", ", found.Select(m => $"{m.Id} ({m.Marker})")));
        }

        return found.Count == 0 ? AntiCheatDetection.None : new AntiCheatDetection(found);
    }

    /// <summary>
    /// Checks available catalog games that were never checked or appear in <paramref name="changes"/> as added or updated
    /// (an update can add an anti-cheat), and stores the anti-cheat ID or <see cref="AntiCheatPolicy.NoneId"/>. Returns
    /// how many games were checked.
    /// </summary>
    public int UpdateCatalog(GameCatalog catalog, IEnumerable<GameChange> changes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(changes);
        var changed = changes.Where(c => c is GameAdded or GameVersionChanged).Select(c => c.Game.Id).ToHashSet();
        var due = catalog.Games.Where(g => g.IsAvailable && (g.AntiCheat is null || changed.Contains(g.Id))).ToList();
        foreach (var game in due)
        {
            var antiCheat = Detect(game.InstallPath).StoredId;
            catalog.Update(game.Id, g => g with { AntiCheat = antiCheat });
        }

        return due.Count;
    }

    /// <summary>A parsed <see cref="AntiCheatRule.Markers"/> entry.</summary>
    private sealed record Marker(string? Parent, string Name, bool IsFolder, bool IsPattern)
    {
        public static Marker Parse(string text)
        {
            var isFolder = text.EndsWith('/');
            var path = isFolder ? text[..^1] : text;
            var slash = path.LastIndexOf('/');
            var name = path[(slash + 1)..];
            return new Marker(slash < 0 ? null : path[..slash], name, isFolder, name.IndexOfAny(['*', '?']) >= 0);
        }

        /// <summary>Relative path of the match inside <paramref name="folder"/>, or <c>null</c>.</summary>
        public string? Find(FolderView folder)
        {
            var parent = Parent is null ? folder : folder.At(Parent);
            if (parent is null)
            {
                return null;
            }

            if (!IsPattern)
            {
                if (!IsFolder)
                {
                    return parent.File(Name);
                }

                return parent.Dir(Name) is { } dir ? dir.Relative + "/" : null;
            }

            var names = IsFolder ? parent.DirNames : parent.FileNames;
            var name = names.FirstOrDefault(n => FileSystemName.MatchesSimpleExpression(Name, n, ignoreCase: true));
            return name is null ? null : parent.RelativeOf(name) + (IsFolder ? "/" : "");
        }
    }
}
