using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The files a generation keeps in its job folder, next to (never inside) the working copy of the game:
/// <c>strings.json</c> = the string store (a plain JSON <see cref="StringTable"/> of the translations so far, written
/// after every change), <c>glossary.json</c> (<see cref="Glossary"/>), <c>source.json</c> (the extracted strings), <c>chunks.json</c> (the translate plan), <c>progress.json</c> (recent
/// translations, agent usage), <c>build/</c> = the latest rebuild, laid out
/// like the game folder, an update job's <c>base/</c> (task 56) and, for a game the toolkit can't read (task 57), the
/// agent's workspace <c>agent/</c> (<see cref="AgentFolder"/>). Writes are atomic. Beside them: <c>job.json</c> and <c>logs/job.log</c> (<see cref="GenerationJobStore"/>).
/// </summary>
public sealed class GenerationFiles
{
    private const string BaseManifestName = "manifest.json";
    private const string BaseStringsName = "strings.json.gz";
    private const string BaseGlossaryName = "glossary.json";

    public GenerationFiles(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        Folder = Path.GetFullPath(jobFolder);
    }

    public string Folder { get; }

    public string StringsPath => Path.Combine(Folder, "strings.json");

    public string GlossaryPath => Path.Combine(Folder, "glossary.json");

    /// <summary>The Extract stage's result: every string with its original, context and limits (task 49, read by the translate stage).</summary>
    public string SourcePath => Path.Combine(Folder, "source.json");

    /// <summary>The Translate stage's chunks (task 50): the keys of each chunk, fixed by the Extract stage.</summary>
    public string ChunksPath => Path.Combine(Folder, "chunks.json");

    /// <summary>What a job shows while it runs (task 50): the strings just translated and the agent's usage so far.</summary>
    public string ProgressPath => Path.Combine(Folder, "progress.json");

    public string BuildFolder => Path.Combine(Folder, "build");

    /// <summary>An update job's base pack (task 56): <c>manifest.json</c>, <c>strings.json.gz</c>, <c>glossary.json</c> when it had one.</summary>
    public string BaseFolder => Path.Combine(Folder, "base");

    /// <summary>
    /// The agent's own workspace for a game the toolkit can't read (task 57), its runs' working folder and the only folder
    /// its commands may write: <c>scripts/</c> (its extract and rebuild code, kept for later promotion into the toolkit,
    /// never in a pack), <c>strings.json</c> (what its extractor found), <c>translations.json</c> (written by the app for
    /// its rebuild), <c>out/</c> (the game files its rebuild wrote).
    /// </summary>
    public string AgentFolder => Path.Combine(Folder, "agent");

    /// <summary>The agent extractor's output: <c>{strings: [{key, original, file?, context?, maxLength?, placeholders?}]}</c>.</summary>
    public string AgentStringsPath => Path.Combine(AgentFolder, "strings.json");

    /// <summary>The translations the agent's rebuild applies: <c>{language, strings: {key: translation}}</c>.</summary>
    public string AgentTranslationsPath => Path.Combine(AgentFolder, "translations.json");

    /// <summary>The game files the agent's rebuild wrote, laid out like the game folder.</summary>
    public string AgentOutputFolder => Path.Combine(AgentFolder, "out");

    /// <summary>The agent's extract and rebuild code.</summary>
    public string AgentScriptsFolder => Path.Combine(AgentFolder, "scripts");

    /// <summary>Writes the base pack's files (<see cref="BaseFolder"/>); the manifest last.</summary>
    /// <exception cref="IOException">A file can't be written.</exception>
    public void WriteBase(UpdateBase updateBase)
    {
        ArgumentNullException.ThrowIfNull(updateBase);
        Directory.CreateDirectory(BaseFolder);
        AtomicFile.Write(Path.Combine(BaseFolder, BaseStringsName), stream => StringTableSerializer.Write(updateBase.Strings, stream));
        var glossary = Path.Combine(BaseFolder, BaseGlossaryName);
        if (updateBase.Glossary is { } g)
        {
            AtomicFile.WriteAllBytes(glossary, GlossarySerializer.Write(g));
        }
        else
        {
            File.Delete(glossary);
        }

        AtomicFile.WriteAllBytes(Path.Combine(BaseFolder, BaseManifestName), System.Text.Encoding.UTF8.GetBytes(PackManifestSerializer.Write(updateBase.Manifest)));
    }

    /// <summary>The base pack's files, or <c>null</c> when the job has none (not an update job).</summary>
    /// <exception cref="GenerationFileException">A file exists but can't be read or isn't valid.</exception>
    public UpdateBase? ReadBase()
    {
        var manifestPath = Path.Combine(BaseFolder, BaseManifestName);
        if (ReadBytes(manifestPath) is not { } manifestData)
        {
            return null;
        }

        var manifest = PackManifestSerializer.Read(System.Text.Encoding.UTF8.GetString(manifestData));
        if (manifest.Value is not { } value)
        {
            throw new GenerationFileException(manifestPath, string.Join("; ", manifest.Errors.Select(e => $"{e.Field}: {e.Message}")));
        }

        var stringsPath = Path.Combine(BaseFolder, BaseStringsName);
        var strings = ReadBytes(stringsPath) is { } stringsData
            ? StringTableSerializer.Read(stringsData)
            : throw new GenerationFileException(stringsPath, "is missing");
        if (strings.Value is not { } table)
        {
            throw new GenerationFileException(stringsPath, string.Join("; ", strings.Errors.Select(e => $"{e.Field}: {e.Message}")));
        }

        var glossaryPath = Path.Combine(BaseFolder, BaseGlossaryName);
        Glossary? glossary = null;
        if (ReadBytes(glossaryPath) is { } glossaryData)
        {
            var (read, error) = GlossarySerializer.Read(glossaryData);
            glossary = read ?? throw new GenerationFileException(glossaryPath, error!);
        }

        return new UpdateBase(value, table, glossary);
    }

    /// <summary>The string store, or <c>null</c> when nothing was stored yet.</summary>
    /// <exception cref="GenerationFileException">The file exists but can't be read or isn't a valid string table.</exception>
    public StringTable? ReadStrings()
    {
        if (ReadBytes(StringsPath) is not { } data)
        {
            return null;
        }

        var result = StringTableSerializer.Read(data);
        return result.Value ?? throw new GenerationFileException(
            StringsPath, string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
    }

    /// <exception cref="IOException">The file can't be written.</exception>
    public void WriteStrings(StringTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        AtomicFile.Write(StringsPath, stream => StringTableSerializer.Write(table, stream, compress: false));
    }

    /// <summary>The glossary, or <c>null</c> when none was written yet.</summary>
    /// <exception cref="GenerationFileException">The file exists but can't be read or isn't a valid glossary.</exception>
    public Glossary? ReadGlossary()
    {
        if (ReadBytes(GlossaryPath) is not { } data)
        {
            return null;
        }

        var (glossary, error) = GlossarySerializer.Read(data);
        return glossary ?? throw new GenerationFileException(GlossaryPath, error!);
    }

    /// <exception cref="IOException">The file can't be written.</exception>
    public void WriteGlossary(Glossary glossary) => AtomicFile.WriteAllBytes(GlossaryPath, GlossarySerializer.Write(glossary));

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GenerationFileException(path, e.Message, e);
        }
    }
}

/// <summary>A job file exists but can't be read or holds something invalid.</summary>
public sealed class GenerationFileException : Exception
{
    public GenerationFileException(string path, string reason, Exception? inner = null)
        : base($"{Path.GetFileName(path)}: {reason}", inner)
    {
        FilePath = path;
    }

    public GenerationFileException()
    {
        FilePath = "";
    }

    public GenerationFileException(string message)
        : base(message)
    {
        FilePath = "";
    }

    public GenerationFileException(string message, Exception inner)
        : base(message, inner)
    {
        FilePath = "";
    }

    public string FilePath { get; }
}
