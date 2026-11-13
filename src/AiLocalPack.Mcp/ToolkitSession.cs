using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;
using ModelContextProtocol;

namespace AiLocalPack.Mcp;

/// <summary>How the server is started (<c>ailocalpack mcp --root … --job … [--lang …]</c>).</summary>
/// <param name="Root">The working copy of the game (the only folder file tools see).</param>
/// <param name="JobFolder">Where the string store, glossary and rebuild go (<see cref="GenerationFiles"/>); not inside <paramref name="Root"/>.</param>
/// <param name="Language">The translation's language tag; <c>null</c> = the stored strings' language (writes are refused while neither is known).</param>
/// <param name="Tools">The machine's recipe tools (<see cref="RecipeTools.Create"/>).</param>
public sealed record ToolkitServerOptions(string Root, string JobFolder, string? Language, RecipeTools Tools);

/// <summary>
/// State shared by the tool calls of one server: the working copy, the job files and the extraction (cached for the
/// session: the copy doesn't change under the toolkit; <c>refresh</c> reads it again). Calls run one at a time.
/// </summary>
public sealed class ToolkitSession
{
    private readonly ToolkitServerOptions options;
    private EngineDetection? detection;
    private Extracted? extraction;
    private AgentScriptTool? agentTool;

    /// <exception cref="ArgumentException">The root is missing, or the job folder is the root or inside it.</exception>
    public ToolkitSession(ToolkitServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        Copy = new WorkingCopy(options.Root);
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

    public WorkingCopy Copy { get; }

    public GenerationFiles Files { get; }

    public RecipeTools Tools => options.Tools;

    /// <summary>Serializes the tool calls (the SDK may run requests concurrently).</summary>
    public object Gate { get; } = new();

    public EngineDetection Detect(bool refresh = false) =>
        detection = refresh || detection is null ? new EngineDetector().Detect(Copy.Root) : detection;

    /// <summary>
    /// The recipe tool for the copy's engine; for an engine the toolkit can't read, the strings the agent's own extractor
    /// wrote into its workspace (<see cref="AgentScriptTool"/>, task 57) once that file is there.
    /// </summary>
    /// <exception cref="McpException">Unknown engine, an engine switched off on this OS, or one without a tool, and no agent extraction.</exception>
    public (IRecipeTool Tool, EngineKind Kind) Tool()
    {
        var kind = Detect().Kind;
        if (ToolProblem(kind) is not { } problem)
        {
            return (Tools.For(kind)!, kind);
        }

        var agent = AgentTool();
        return agent.Exists
            ? (agent, kind)
            : throw new McpException(problem + " Write your own extractor: it must write strings.json in your workspace.");
    }

    /// <summary>The strings the agent's extractor wrote (<c>agent/strings.json</c> of the job folder).</summary>
    public AgentScriptTool AgentTool() => agentTool ??= new AgentScriptTool(Files.AgentStringsPath);

    /// <summary>Why the toolkit can't read games of <paramref name="kind"/>, or <c>null</c> when it can.</summary>
    public string? ToolProblem(EngineKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Engine == GameEngine.Unknown ? "The engine is unknown, so the toolkit can't read its strings."
            : Tools.IsDisabled(kind) ? $"The toolkit doesn't handle {kind.DisplayName} games on this system for now."
            : Tools.For(kind) is null ? $"The toolkit can't read {kind.DisplayName} games yet."
            : null;
    }

    /// <summary>The copy's strings (cached).</summary>
    /// <exception cref="McpException">No tool for the engine, or the game files can't be read.</exception>
    public Extracted Extract(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (extraction is not null && !refresh)
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
            var result = tool.Extract(Copy.Root, cancellationToken);
            return extraction = new Extracted(tool, kind, result, result.Strings.ToDictionary(s => s.Key, StringComparer.Ordinal));
        }
        catch (RecipeException e)
        {
            throw new McpException(e.File is { } file ? $"{file}: {e.Message}" : e.Message, e);
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
