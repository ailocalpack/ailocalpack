using AiLocalPack.Core.Games;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Engines;

/// <summary>
/// Tells which engine a game folder uses from marker files (see the rule classes: Unity, Unreal, RPG Maker MV / MZ,
/// Ren'Py, Godot). Looks at the folder, its direct subfolders (games shipped in <c>Game/</c>, <c>x64/</c>…) and the macOS
/// <c>.app</c> bundles in both. A match nearer the top and with higher confidence wins; two different engines tied on
/// both give an ambiguous result (unknown engine, candidates listed). Best effort: unreadable or missing folders give
/// an unknown engine, never an exception.
/// </summary>
public sealed class EngineDetector(ILogger<EngineDetector>? logger = null)
{
    private static readonly EngineRule[] Rules = [new UnityRule(), new UnrealRule(), new RpgMakerRule(), new RenPyRule(), new GodotRule()];

    private readonly ILogger _logger = logger ?? NullLogger<EngineDetector>.Instance;

    public EngineDetection Detect(string gameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var root = FolderView.Root(Path.GetFullPath(gameFolder));
        var found = new List<(EngineMatch Match, int Depth)>();
        if (IsBundle(root.Name) || root.FileAt("Contents/Info.plist") is not null)
        {
            // The player picked Game.app itself, or Steam installed the bundle's Contents/ straight into the game folder.
            Add(found, Rules.SelectMany(r => r.MatchApp(root)), 0);
        }
        else
        {
            Collect(root, 0, found);
            foreach (var name in root.DirNames.Where(n => !IsBundle(n)))
            {
                Collect(root.Dir(name)!, 1, found);
            }
        }

        var ranked = found
            .Select((f, order) => (f.Match, f.Depth, Order: order))
            .OrderByDescending(f => f.Match.Confidence)
            .ThenBy(f => f.Depth)
            .ThenBy(f => f.Order)
            .DistinctBy(f => f.Match.Kind.Engine)
            .ToList();
        if (ranked.Count == 0)
        {
            _logger.LogDebug("No engine found in {Folder}", gameFolder);
            return EngineDetection.None;
        }

        var best = ranked[0];
        var ambiguous = ranked.Skip(1).Any(f => f.Match.Confidence == best.Match.Confidence && f.Depth == best.Depth);
        var candidates = ranked.Select(f => f.Match).ToList();
        if (ambiguous)
        {
            _logger.LogInformation(
                "Ambiguous engine in {Folder}: {Engines}", gameFolder, string.Join(", ", candidates.Select(c => c.Kind)));
            return new EngineDetection(null, candidates);
        }

        _logger.LogDebug("Engine {Engine} ({Marker}) in {Folder}", best.Match.Kind, best.Match.Marker, gameFolder);
        return new EngineDetection(best.Match, candidates);
    }

    /// <summary>
    /// Detects the engine of available catalog games that have none stored yet or appear in <paramref name="changes"/> as
    /// added or updated (a new build may have moved to another engine version), and stores its ID
    /// (<see cref="EngineKind"/> text, <c>unknown</c> when nothing matched). Returns how many games were checked.
    /// </summary>
    public int UpdateCatalog(GameCatalog catalog, IEnumerable<GameChange> changes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(changes);
        var changed = changes.Where(c => c is GameAdded or GameVersionChanged).Select(c => c.Game.Id).ToHashSet();
        var due = catalog.Games.Where(g => g.IsAvailable && (g.Engine is null || changed.Contains(g.Id))).ToList();
        foreach (var game in due)
        {
            var engine = Detect(game.InstallPath).Kind.ToString();
            catalog.Update(game.Id, g => g with { Engine = engine });
        }

        return due.Count;
    }

    private static void Collect(FolderView folder, int depth, List<(EngineMatch, int)> found)
    {
        Add(found, Rules.SelectMany(r => r.MatchFolder(folder)), depth);
        foreach (var name in folder.DirNames.Where(IsBundle))
        {
            var app = folder.Dir(name)!;
            Add(found, Rules.SelectMany(r => r.MatchApp(app)), depth);
        }
    }

    private static void Add(List<(EngineMatch, int)> found, IEnumerable<EngineMatch> matches, int depth) =>
        found.AddRange(matches.Select(m => (m, depth)));

    private static bool IsBundle(string name) => name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
}
