using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Generation;

/// <summary>Where the text size of a <see cref="GenerationSizeEstimate"/> comes from.</summary>
public enum TextSizeSource
{
    /// <summary>The toolkit extracted the strings.</summary>
    Toolkit,

    /// <summary>The engine is unknown: guessed from file sizes.</summary>
    UnknownEngine,

    /// <summary>The toolkit doesn't handle the engine on this OS (<see cref="RecipeTools.DisabledOn"/>): guessed.</summary>
    EngineDisabled,

    /// <summary>The engine is detected but has no recipe tool: guessed.</summary>
    NoTool,

    /// <summary>The toolkit failed to read the game (<see cref="GenerationSizeEstimate.ToolkitError"/>): guessed.</summary>
    ToolkitFailed,
}

/// <summary>Everything the start form shows before a job: text to translate, its cost, the working copy and the disk.</summary>
/// <param name="Tool">The recipe tool that read the strings, when the toolkit handles the engine.</param>
/// <param name="ToolkitError">The toolkit's message when it failed (<see cref="TextSizeSource.ToolkitFailed"/>).</param>
/// <param name="ReserveBytes">Room counted for the job's rebuilt files (the files the strings come from).</param>
public sealed record GenerationSizeEstimate(
    EngineKind Engine,
    TextSizeSource Source,
    IRecipeTool? Tool,
    string? ToolkitError,
    GenerationEstimate Work,
    WorkingCopyPlan Copy,
    WorkingCopySpace Space,
    long ReserveBytes)
{
    /// <summary>
    /// An update's diff against the base pack (task 56), when one was given and the toolkit read the strings:
    /// <see cref="Work"/> then counts only the strings to translate (added and changed).
    /// </summary>
    public UpdateDiff? Update { get; init; }
}

/// <summary>
/// Sizes up a generation before it starts: the strings and words through the toolkit's extract when it handles the
/// engine (else a guess from file sizes, <see cref="GenerationEstimateModel.GuessFromFileSizes"/>), the cost from
/// <see cref="GenerationEstimateModel"/>, the working copy (<see cref="WorkingCopyScope.WithoutMedia"/> when the toolkit
/// reads the game, else every file) and the disk. Reads the game as it is without the pack installed in it
/// (<see cref="WorkingCopies.ReadWithoutPack{T}"/>: a scratch copy with the pack's originals when one is installed);
/// blocks, call it off the UI thread.
/// </summary>
public sealed class GenerationEstimator(RecipeTools tools, WorkingCopies copies, EngineDetector? detector = null)
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".toml", ".po", ".pot", ".lang", ".loc",
        ".strings", ".properties", ".lua", ".js", ".py", ".rb", ".ink", ".rpy", ".tscn", ".tres", ".dialogue", ".md", ".html", ".htm",
    };

    private static readonly HashSet<string> NonTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images.
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".dds", ".ktx", ".ktx2", ".psd", ".gif", ".tif", ".tiff", ".exr", ".hdr", ".ico", ".icns", ".svg",

        // Programs and libraries.
        ".exe", ".dll", ".so", ".dylib", ".pdb", ".a", ".lib", ".node", ".sys", ".msi", ".app", ".sh", ".bat", ".cmd",

        // Fonts, shader caches.
        ".ttf", ".otf", ".ttc", ".woff", ".woff2", ".fnt", ".shadercache", ".pso",
    };

    private readonly EngineDetector _detector = detector ?? new EngineDetector();

    /// <param name="engine">The game's engine as stored (<c>Game.Engine</c>), else it is detected.</param>
    /// <param name="scope">The working copy's scope, else <see cref="WorkingCopyScope.WithoutMedia"/> when the toolkit reads the game and <see cref="WorkingCopyScope.AllFiles"/> otherwise.</param>
    /// <param name="updateBase">An update's base string table (task 56): only the strings it lacks or that changed count.</param>
    /// <exception cref="WorkingCopyException">The game folder is missing or can't be listed.</exception>
    public GenerationSizeEstimate Estimate(
        string gameFolder,
        EngineKind? engine = null,
        WorkingCopyScope? scope = null,
        CancellationToken cancellationToken = default,
        Core.Packs.StringTable? updateBase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var root = Path.GetFullPath(gameFolder);
        if (!Directory.Exists(root))
        {
            throw new WorkingCopyException(WorkingCopyProblem.GameMissing, $"The game folder {root} doesn't exist.", root);
        }

        var kind = engine is { Engine: not GameEngine.Unknown } ? engine : _detector.Detect(root).Kind;
        var (source, tool, extraction, error) = Extract(root, kind, cancellationToken);
        var plan = copies.Plan(root, scope ?? (extraction is null ? WorkingCopyScope.AllFiles : WorkingCopyScope.WithoutMedia), cancellationToken);

        GenerationEstimate work;
        UpdateDiff? update = null;
        long reserve = 0;
        if (extraction is not null)
        {
            var strings = extraction.Strings;
            if (updateBase is not null)
            {
                update = UpdateDiff.Of(strings.Select(s => KeyValuePair.Create(s.Key, s.Original)), updateBase);
                strings = [.. strings.Where(s => update.ToTranslate.Contains(s.Key))];
            }

            work = GenerationEstimateModel.Estimate(TextSize.Of(strings.Select(s => s.Original)), EstimateBasis.Toolkit);
            reserve = extraction.Files.Sum(f => SizeOf(Path.Combine(root, f)));
        }
        else
        {
            work = GenerationEstimateModel.Estimate(Guess(root, cancellationToken), EstimateBasis.FileSizes);
        }

        return new GenerationSizeEstimate(kind, source, tool, error, work, plan, copies.CheckSpace(plan, reserve), reserve) { Update = update };
    }

    /// <summary>Text file and other data file bytes of the game (media, images, programs and fonts left out), for the guess.</summary>
    internal static TextSize Guess(string root, CancellationToken cancellationToken)
    {
        long text = 0, data = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TextExtensions.Contains(file.Extension))
            {
                text += file.Length;
            }
            else if (!NonTextExtensions.Contains(file.Extension) && !WorkingCopies.IsMedia(file.Name))
            {
                data += file.Length;
            }
        }

        return GenerationEstimateModel.GuessFromFileSizes(text, data);
    }

    private (TextSizeSource, IRecipeTool?, RecipeExtraction?, string?) Extract(string root, EngineKind kind, CancellationToken cancellationToken)
    {
        if (kind.Engine == GameEngine.Unknown)
        {
            return (TextSizeSource.UnknownEngine, null, null, null);
        }

        if (tools.IsDisabled(kind))
        {
            return (TextSizeSource.EngineDisabled, null, null, null);
        }

        if (tools.For(kind) is not { } tool)
        {
            return (TextSizeSource.NoTool, null, null, null);
        }

        try
        {
            // The game's own text, not a translation installed over it (task 56).
            return (TextSizeSource.Toolkit, tool, copies.ReadWithoutPack(root, folder => tool.Extract(folder, cancellationToken), cancellationToken), null);
        }
        catch (RecipeException e)
        {
            return (TextSizeSource.ToolkitFailed, tool, null, e.Message);
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
