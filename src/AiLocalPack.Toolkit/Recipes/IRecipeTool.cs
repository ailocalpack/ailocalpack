using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Recipes;

/// <summary>
/// An engine's extract / rebuild code, named by a pack's recipe (<c>recipe.tool</c> + <c>recipe.version</c> in the
/// manifest). Extract reads the player's game files into keyed strings; rebuild writes the game files again with a
/// string table's translations, reading the player's own copy. Only built-in code: nothing from a pack runs.
/// Methods read the disk and block; call them off the UI thread.
/// </summary>
public interface IRecipeTool
{
    /// <summary>Tool ID (a slug), the manifest's <c>recipe.tool</c>.</summary>
    string Id { get; }

    /// <summary>Tool version (<c>major.minor.patch</c>), the manifest's <c>recipe.version</c> for packs made with it.</summary>
    Version Version { get; }

    /// <summary>The engine whose games the tool handles.</summary>
    GameEngine Engine { get; }

    /// <summary>Every translatable string of the game, keys in ordinal order.</summary>
    /// <exception cref="RecipeException">Not a game of this engine, or a file can't be read or parsed.</exception>
    RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes each game file that the translations change (or add) to <paramref name="output"/> (by its path relative
    /// to the game folder). Only keys the extractor gives are applied; others are reported in <see cref="RebuildResult.UnknownKeys"/>.
    /// An empty translation, or one equal to the original, leaves the string as it is.
    /// </summary>
    /// <param name="language">The translations' language tag (the string table's); engines with their own translation system install under it.</param>
    /// <exception cref="RecipeException">Not a game of this engine, or a file can't be read, parsed or written.</exception>
    RebuildResult Rebuild(string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default);

    /// <summary>
    /// The game files (relative, <c>/</c>-separated) holding <paramref name="keys"/>: what a pack with these keys
    /// rewrites, so the files whose hashes its manifest lists. Keys of files that don't exist are left out.
    /// </summary>
    /// <exception cref="RecipeException">Not a game of this engine.</exception>
    IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys);
}

/// <summary>One translatable string.</summary>
/// <param name="Key">Stable key: the same text gets the same key on every run, OS and file order.</param>
/// <param name="Original">The text in the game files.</param>
/// <param name="Context">What the text is, in English, for the translator (e.g. <c>Item description</c>).</param>
/// <param name="MaxLength">Length limit in characters when the engine has one, else <c>null</c>.</param>
/// <param name="Placeholders">Escape codes and format tokens in the original, in order (a translation keeps each).</param>
public sealed record ExtractedString(string Key, string Original, string? Context, int? MaxLength, IReadOnlyList<string> Placeholders);

/// <summary>Result of <see cref="IRecipeTool.Extract"/>.</summary>
/// <param name="Strings">Keys in ordinal order.</param>
/// <param name="Files">Game files the strings come from (relative, <c>/</c>-separated, ordinal order).</param>
public sealed record RecipeExtraction(string Tool, Version Version, IReadOnlyList<ExtractedString> Strings, IReadOnlyList<string> Files);

/// <summary>Result of <see cref="IRecipeTool.Rebuild"/>.</summary>
/// <param name="ChangedFiles">Files written to the output (relative to the game folder, ordinal order).</param>
/// <param name="Applied">Translations that changed a string.</param>
/// <param name="UnknownKeys">Translation keys the game doesn't have (left out; ordinal order).</param>
public sealed record RebuildResult(IReadOnlyList<string> ChangedFiles, int Applied, IReadOnlyList<string> UnknownKeys)
{
    /// <summary>
    /// Files the game itself makes from the written ones when it runs (Ren'Py compiles <c>x.rpy</c> into <c>x.rpyc</c>),
    /// relative to the game folder: uninstalling the pack deletes them too.
    /// </summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];
}

/// <summary>Where <see cref="IRecipeTool.Rebuild"/> puts the rebuilt files.</summary>
public interface IRebuildOutput
{
    /// <param name="relativePath">Path relative to the game folder, <c>/</c>-separated.</param>
    void Write(string relativePath, ReadOnlySpan<byte> content);
}

/// <summary>Writes rebuilt files into a folder laid out like the game folder (the staging folder of an install).</summary>
public sealed class DirectoryRebuildOutput(string root) : IRebuildOutput
{
    public string Root { get; } = Path.GetFullPath(root);

    public void Write(string relativePath, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Path escapes the output folder: '{relativePath}'.", nameof(relativePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(content);
    }
}

/// <summary>A recipe tool couldn't run: wrong engine, or a game file that can't be read, parsed or written.</summary>
public sealed class RecipeException : Exception
{
    public RecipeException(string message, string? file = null, Exception? inner = null)
        : base(message, inner)
    {
        File = file;
    }

    public RecipeException()
    {
    }

    public RecipeException(string message)
        : base(message)
    {
    }

    public RecipeException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>The game file (relative, <c>/</c>-separated) at fault, when there is one.</summary>
    public string? File { get; }
}
