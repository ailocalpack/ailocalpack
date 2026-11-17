using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;
using ModelContextProtocol;

namespace AiLocalPack.Mcp;

/// <summary>How the server is started (<c>ailocalpack mcp --root … --job … [--lang …] [--tool …]</c>).</summary>
/// <param name="Root">The game folder (the only folder file tools see; read only).</param>
/// <param name="JobFolder">Where the string store, glossary and rebuild go (<see cref="GenerationFiles"/>); not inside <paramref name="Root"/>.</param>
/// <param name="Language">The translation's language tag; <c>null</c> = the stored strings' language (writes are refused while neither is known).</param>
/// <param name="Tools">The machine's recipe tools (<see cref="RecipeTools.Create"/>).</param>
public sealed record ToolkitServerOptions(string Root, string JobFolder, string? Language, RecipeTools Tools)
{
    /// <summary>The job's <see cref="GenerationOptions.Reader"/>: the tool that reads the game, <see cref="GenerationOptions.AgentReader"/>, or <c>null</c> = the detected engine's tool.</summary>
    public string? Reader { get; init; }

    /// <summary>How many game runs the agent may ask for with <c>test_run</c> (<c>--test-runs</c>); <c>null</c> = the tool isn't offered.</summary>
    public int? TestRuns { get; init; }

    /// <summary>
    /// The game as the tools read it: <see cref="Root"/> without the pack installed there (<see cref="GameViews"/>,
    /// _Live game folder_), so the strings and the files' text are the game's own; <c>null</c> = the folder as it is.
    /// </summary>
    public IGameFiles? Game { get; init; }
}

/// <summary>
/// State shared by the tool calls of one server: the game folder, the job files and the extraction (cached for the
/// session: the toolkit doesn't change the game; <c>refresh</c> reads it again). Calls run one at a time.
/// </summary>
public sealed class ToolkitSession
{
    private readonly ToolkitServerOptions options;
    private EngineDetection? detection;
    private Extracted? extraction;
    private DateTime? limitsStamp;

    /// <summary>When <c>limits.json</c> was last written, or <c>null</c> without one.</summary>
    private DateTime? LimitsStamp()
    {
        try
        {
            return File.Exists(Files.LimitsPath) ? File.GetLastWriteTimeUtc(Files.LimitsPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
    private AgentScriptTool? agentTool;

    /// <exception cref="ArgumentException">The root is missing, or the job folder is the root or inside it.</exception>
    public ToolkitSession(ToolkitServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        Copy = new GameRoot(options.Root);
        if (!Directory.Exists(Copy.Root))
        {
            throw new ArgumentException($"Folder not found: {Copy.Root}", nameof(options));
        }

        Files = new GenerationFiles(options.JobFolder);
        var job = Path.TrimEndingDirectorySeparator(Files.Folder);
        if (string.Equals(job, Copy.Root, StringComparison.OrdinalIgnoreCase)
            || job.StartsWith(Copy.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The job folder must not be the game folder or inside it.", nameof(options));
        }

        if (options.Language is { } language && StoredLanguage() is { } stored && stored != language)
        {
            throw new ArgumentException($"The job's strings.json is in '{stored}', not '{language}'.", nameof(options));
        }
    }

    public GameRoot Copy { get; }

    /// <summary>The game as the tools read it (<see cref="ToolkitServerOptions.Game"/>, else the folder as it is).</summary>
    public IGameFiles Game => game ??= options.Game ?? GameFiles.Folder(Copy.Root);

    private IGameFiles? game;

    public GenerationFiles Files { get; }

    public RecipeTools Tools => options.Tools;

    /// <summary>Whether <c>test_run</c> is offered (<see cref="ToolkitServerOptions.TestRuns"/>).</summary>
    public int? TestRuns => options.TestRuns;

    /// <summary>Serializes the tool calls (the SDK may run requests concurrently).</summary>
    public object Gate { get; } = new();

    public EngineDetection Detect(bool refresh = false) =>
        detection = refresh || detection is null ? new EngineDetector().Detect(Copy.Root) : detection;

    /// <summary>
    /// The strings the agent's own extractor wrote into its workspace (<see cref="AgentScriptTool"/>, task 57) once that
    /// file is there: the job went the agent's way, for an engine the toolkit can't read or one whose tool failed on this
    /// game. Otherwise the recipe tool for the game's engine.
    /// </summary>
    /// <exception cref="McpException">Unknown engine, an engine switched off on this OS, or one without a tool, and no agent extraction.</exception>
    public (IRecipeTool Tool, EngineKind Kind) Tool()
    {
        var reader = Reader();
        var agent = AgentTool();
        if (agent.Exists)
        {
            return (agent, reader.Kind);
        }

        return reader.Tool is { } tool
            ? (tool, reader.Kind)
            : throw new McpException(reader.Problem + WriteYourOwn);
    }

    /// <summary>What reads the game: the job's choice (<see cref="ToolkitServerOptions.Reader"/>), else the detected engine's tool.</summary>
    public GameReader Reader() => GameReader.Resolve(Tools, Detect().Kind, options.Reader);

    private const string WriteYourOwn = " Write your own extractor: it must write strings.json in your workspace.";

    /// <summary>The job's reference packs (<c>refs/</c> of the job folder, read once; best effort).</summary>
    public ReferenceSet References() => references ??= Files.ReadReferences();

    private ReferenceSet? references;

    /// <summary>The strings the agent's extractor wrote (<c>agent/strings.json</c> of the job folder).</summary>
    public AgentScriptTool AgentTool() => agentTool ??= new AgentScriptTool(Files.AgentStringsPath);

    /// <summary>The game's strings (cached).</summary>
    /// <exception cref="McpException">No tool for the engine, or the game files can't be read.</exception>
    public Extracted Extract(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (extraction is not null && !refresh && LimitsStamp() == limitsStamp)
        {
            return extraction;
        }

        var (tool, kind) = Tool();
        if (refresh && tool is AgentScriptTool)
        {
            // The extractor may have written the file again: a new instance reads it afresh.
            agentTool = null;
            (tool, kind) = Tool();
        }

        try
        {
            var raw = tool.Extract(Game, cancellationToken);
            // The limits the agent learned (limits.json) are checked on every string; read again when the file changes.
            limitsStamp = LimitsStamp();
            var result = raw with { Strings = raw.Strings.WithLimits(StringLimitsFile.Read(Files.Folder)) };
            return extraction = new Extracted(tool, kind, result, result.Strings.ToDictionary(s => s.Key, StringComparer.Ordinal));
        }
        catch (RecipeException e)
        {
            var message = e.File is { } file ? $"{file}: {e.Message}" : e.Message;
            throw new McpException(tool is AgentScriptTool ? message : $"The toolkit couldn't read the game: {message}" + WriteYourOwn, e);
        }
    }

    /// <summary>The stored translations (empty when nothing was stored).</summary>
    /// <exception cref="McpException">The store can't be read.</exception>
    public StringTable ReadStrings()
    {
        try
        {
            return Files.ReadStrings() ?? new StringTable(Language() ?? "", new Dictionary<string, string>());
        }
        catch (GenerationFileException e)
        {
            throw new McpException($"The string store can't be read: {e.Message}", e);
        }
    }

    /// <summary>The target language for writes.</summary>
    /// <exception cref="McpException">Unknown (no <c>--lang</c> and nothing stored yet).</exception>
    public string WriteLanguage() =>
        Language() ?? throw new McpException("No target language: the server was started without --lang and nothing is stored yet.");

    public void WriteStrings(StringTable table) => Write(() => Files.WriteStrings(table), "strings.json");

    /// <summary>The string store's lock for a read, change and write (<see cref="GenerationFiles.LockStrings"/>).</summary>
    /// <exception cref="McpException">Another writer holds it too long, or it can't be made.</exception>
    public IDisposable LockStrings()
    {
        try
        {
            return Files.LockStrings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"Can't lock strings.json: {e.Message}", e);
        }
    }

    /// <exception cref="McpException">The glossary can't be read.</exception>
    public Glossary? ReadGlossary()
    {
        try
        {
            return Files.ReadGlossary();
        }
        catch (GenerationFileException e)
        {
            throw new McpException($"The glossary can't be read: {e.Message}", e);
        }
    }

    public void WriteGlossary(Glossary glossary) => Write(() => Files.WriteGlossary(glossary), "glossary.json");

    private string? Language() => options.Language ?? StoredLanguage();

    private string? StoredLanguage()
    {
        try
        {
            return Files.ReadStrings()?.Language;
        }
        catch (GenerationFileException)
        {
            return null;
        }
    }

    private static void Write(Action write, string name)
    {
        try
        {
            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"Can't save {name}: {e.Message}", e);
        }
    }
}

/// <summary>An extraction with its strings by key.</summary>
public sealed record Extracted(IRecipeTool Tool, EngineKind Kind, RecipeExtraction Result, IReadOnlyDictionary<string, ExtractedString> ByKey);
