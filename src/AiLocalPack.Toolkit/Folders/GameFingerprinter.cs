using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Folders;

/// <summary>Hashing progress: <paramref name="FilesDone"/> of <paramref name="FilesTotal"/> files.</summary>
public readonly record struct HashProgress(int FilesDone, int FilesTotal);

/// <summary>
/// Identity and version of a manually added game (Steam games use the app ID and build ID instead).
/// <list type="bullet">
/// <item><b>Fingerprint</b> (<c>fp:&lt;sha-256&gt;</c>): SHA-256 over the main executable's hash and engine identity data
/// that names the game but not its content: Unity <c>&lt;Name&gt;_Data/app.info</c> (company and product), the RPG Maker
/// <c>gameTitle</c> (stock engine executables are the same in every game of an engine version). Paths are relative to the
/// game folder, so copies at different places get the same fingerprint, and content updates that leave the executable
/// alone keep it.</item>
/// <item><b>Version</b>: file-hash set of the main executable and the files packs touch, per engine (Unity assets and
/// managed / IL2CPP metadata, Unreal <c>&lt;Project&gt;/Content/Paks</c>, RPG Maker <c>data/*.json</c> and <c>js/plugins.js</c>, Ren'Py
/// <c>game/</c> scripts and archives, Godot <c>.pck</c>).</item>
/// </list>
/// Both are computed only when asked for (hashing can read gigabytes) and go through a <see cref="FileHashCache"/>, so
/// unchanged files (same size and last write time) are not read again. Throws <see cref="IOException"/> when a needed file
/// can't be read (a wrong identity is worse than none) and <see cref="OperationCanceledException"/> when cancelled.
/// </summary>
public sealed class GameFingerprinter(FileHashCache? cache = null, ILogger<GameFingerprinter>? logger = null)
{
    private const string FormatTag = "ailocalpack-fingerprint-1";

    /// <summary>Deepest folder level searched for key files (the game folder is 0).</summary>
    private const int MaxDepth = 8;

    private readonly FileHashCache _cache = cache ?? new FileHashCache();
    private readonly ILogger _logger = logger ?? NullLogger<GameFingerprinter>.Instance;

    public GameId Fingerprint(FoundGame game, IProgress<HashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Fingerprint(game.Path, game.Engine.Kind, game.MainExecutable, progress, cancellationToken);
    }

    /// <param name="gameFolder">The game folder.</param>
    /// <param name="engine">Its engine (<see cref="EngineKind.Unknown"/> if not known).</param>
    /// <param name="mainExecutable">Main executable relative to the game folder (a macOS <c>.app</c> bundle is fine), or <c>null</c>.</param>
    public GameId Fingerprint(
        string gameFolder,
        EngineKind engine,
        string? mainExecutable,
        IProgress<HashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(engine);
        var root = Path.GetFullPath(gameFolder);
        var files = ListFiles(root);
        var identity = new List<string>();
        if (ExecutableFile(root, mainExecutable) is { } exe)
        {
            identity.Add(exe);
        }

        identity.AddRange(files.Where(f => IsIdentityFile(engine, f)));
        if (identity.Count == 0)
        {
            // No executable: the content is all there is to tell the game by.
            identity.AddRange(files.Where(f => IsVersionFile(engine, f)));
        }

        var lines = HashAll(root, identity.Distinct(StringComparer.Ordinal).ToList(), progress, cancellationToken)
            .Select(h => $"file\t{h.Key.ToLowerInvariant()}\t{h.Value}")
            .ToList();
        if (engine.Engine == GameEngine.RpgMaker && RpgMakerTitle(root, files) is { } title)
        {
            lines.Add($"title\t{title}");
        }

        if (lines.Count == 0)
        {
            throw new IOException($"No executable or key file to fingerprint in '{root}'.");
        }

        lines.Sort(StringComparer.Ordinal);
        var text = string.Join('\n', [FormatTag, engine.Engine.ToString().ToLowerInvariant(), .. lines]);
        var id = GameId.Fingerprint(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        _logger.LogDebug("Fingerprint {Id} of {Folder} from {Count} entries", id, root, lines.Count);
        return id;
    }

    public GameVersion? Version(FoundGame game, IProgress<HashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Version(game.Path, game.Engine.Kind, game.MainExecutable, progress, cancellationToken);
    }

    /// <summary>File-hash version, or <c>null</c> when there is no executable and no file packs touch.</summary>
    public GameVersion? Version(
        string gameFolder,
        EngineKind engine,
        string? mainExecutable,
        IProgress<HashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(engine);
        var root = Path.GetFullPath(gameFolder);
        var files = VersionFiles(root, engine, mainExecutable);
        return files.Count == 0 ? null : GameVersion.FromFileHashes(HashAll(root, files, progress, cancellationToken));
    }

    /// <summary>
    /// The files a version is made of: the main executable (a bundle's binary) and the engine's files packs touch,
    /// relative to the game folder with '/' separators, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> VersionFiles(string gameFolder, EngineKind engine, string? mainExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(engine);
        var root = Path.GetFullPath(gameFolder);
        var files = ListFiles(root).Where(f => IsVersionFile(engine, f)).ToList();
        if (ExecutableFile(root, mainExecutable) is { } exe)
        {
            files.Add(exe);
        }

        return [.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private List<KeyValuePair<string, string>> HashAll(
        string root, IReadOnlyList<string> files, IProgress<HashProgress>? progress, CancellationToken cancellationToken)
    {
        var hashes = new List<KeyValuePair<string, string>>(files.Count);
        progress?.Report(new HashProgress(0, files.Count));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes.Add(new(file, _cache.Hash(Path.Combine(root, file), cancellationToken)));
            progress?.Report(new HashProgress(hashes.Count, files.Count));
        }

        return hashes;
    }

    /// <summary>The executable file to hash: the file itself, or for a <c>.app</c> bundle the first binary in <c>Contents/MacOS</c>.</summary>
    private static string? ExecutableFile(string root, string? mainExecutable)
    {
        if (string.IsNullOrEmpty(mainExecutable))
        {
            return null;
        }

        var relative = mainExecutable.Replace('\\', '/').Trim('/');
        var full = Path.Combine(root, relative);
        if (!Directory.Exists(full))
        {
            return relative; // A missing file fails when hashed.
        }

        var macOs = FolderView.Root(full).At("Contents/MacOS");
        return macOs?.FileNames.FirstOrDefault() is { } binary ? $"{relative}/Contents/MacOS/{binary}" : null;
    }

    /// <summary>Every file under <paramref name="root"/> (down to <see cref="MaxDepth"/>), relative with '/' separators.</summary>
    private static List<string> ListFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
        };
        try
        {
            return [.. Directory.EnumerateFiles(root, "*", options).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    private static bool IsIdentityFile(EngineKind engine, string file) =>
        engine.Engine == GameEngine.Unity && UnityDataPath(Segments(file)) is ["app.info"];

    private static bool IsVersionFile(EngineKind engine, string file)
    {
        var parts = Segments(file);
        var name = parts[^1];
        return engine.Engine switch
        {
            GameEngine.Unity => UnityDataPath(parts) switch
            {
                [var single] => single.EndsWith(".assets", StringComparison.Ordinal)
                    || single is "globalgamemanagers" or "data.unity3d" or "maindata",
                ["managed", "assembly-csharp.dll"] => true,
                ["il2cpp_data", "metadata", "global-metadata.dat"] => true,
                _ => false,
            },
            GameEngine.Unreal => parts.Length >= 3 && parts[^3] == "content" && parts[^2] == "paks" && !parts.Contains("engine")
                && (name.EndsWith(".pak", StringComparison.Ordinal) || name.EndsWith(".utoc", StringComparison.Ordinal)
                    || name.EndsWith(".ucas", StringComparison.Ordinal)),
            GameEngine.RpgMaker => parts.Length >= 2
                && ((parts[^2] == "data" && name.EndsWith(".json", StringComparison.Ordinal)) || (parts[^2] == "js" && name == "plugins.js")),
            GameEngine.RenPy => Array.FindIndex(parts, p => p == "game") is var game && game >= 0 && game < parts.Length - 1
                && !parts.Take(game).Contains("renpy")
                && (name.EndsWith(".rpa", StringComparison.Ordinal) || name.EndsWith(".rpyc", StringComparison.Ordinal)
                    || name.EndsWith(".rpy", StringComparison.Ordinal)),
            GameEngine.Godot => name.EndsWith(".pck", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>The path inside a Unity data folder (<c>&lt;Name&gt;_Data/</c> or the bundle's <c>Resources/Data/</c>), or empty.</summary>
    private static string[] UnityDataPath(string[] parts)
    {
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if ((parts[i].Length > 5 && parts[i].EndsWith("_data", StringComparison.Ordinal)) || (parts[i] == "data" && i > 0 && parts[i - 1] == "resources"))
            {
                return parts[(i + 1)..];
            }
        }

        return [];
    }

    /// <summary>The <c>gameTitle</c> of the first <c>data/System.json</c>, or <c>null</c>.</summary>
    private static string? RpgMakerTitle(string root, List<string> files)
    {
        foreach (var file in files.Where(f => Segments(f) is [.., "data", "system.json"]).Order(StringComparer.Ordinal))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, file)));
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("gameTitle", out var title)
                    && title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable or not JSON: no title.
            }
        }

        return null;
    }

    private static string[] Segments(string relative) => relative.ToLowerInvariant().Split('/');
}
