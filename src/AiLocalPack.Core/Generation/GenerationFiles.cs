using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The files a generation keeps in its job folder, next to (never inside) the working copy of the game:
/// <c>strings.json</c> = the string store (a plain JSON <see cref="StringTable"/> of the translations so far, written
/// after every change), <c>glossary.json</c> (<see cref="Glossary"/>), <c>source.json</c> (the extracted strings), <c>chunks.json</c> (the translate plan), <c>progress.json</c> (recent
/// translations, agent usage) and <c>build/</c> = the latest rebuild, laid out
/// like the game folder. Writes are atomic. Beside them: <c>job.json</c> and <c>logs/job.log</c> (<see cref="GenerationJobStore"/>).
/// </summary>
public sealed class GenerationFiles
{
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
