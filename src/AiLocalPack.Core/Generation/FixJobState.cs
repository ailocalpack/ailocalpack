using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The part of a job folder a fix may change (task 75): the string store <c>strings.json</c>, the glossary
/// <c>glossary.json</c> and the agent's own scripts <c>agent/scripts/</c> (unknown engines). Copied aside before a fix
/// runs (so a failed or stopped fix leaves the draft as it was), into a plan's <c>pending/</c>, and with each draft.
/// </summary>
public static class FixJobState
{
    private const string Strings = "strings.json";
    private const string GlossaryFile = "glossary.json";
    private const string Scripts = "scripts";

    /// <summary>Copies the job's state into <paramref name="destination"/> (replaced).</summary>
    /// <exception cref="IOException">A file can't be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be copied.</exception>
    public static void Capture(GenerationFiles files, string destination)
    {
        ArgumentNullException.ThrowIfNull(files);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(staging);
            CopyIfExists(files.StringsPath, Path.Combine(staging, Strings));
            CopyIfExists(files.GlossaryPath, Path.Combine(staging, GlossaryFile));
            if (Directory.Exists(files.AgentScriptsFolder))
            {
                FolderCopy.Replace(files.AgentScriptsFolder, Path.Combine(staging, Scripts));
            }

            FolderCopy.Delete(destination);
            Directory.Move(staging, destination);
        }
        finally
        {
            FolderCopy.Delete(staging);
        }
    }

    /// <summary>Puts a captured state back into the job folder (a file the capture lacks is deleted from the job).</summary>
    /// <exception cref="IOException">A file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be written.</exception>
    public static void Restore(string source, GenerationFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!Directory.Exists(source))
        {
            throw new IOException($"The saved state {source} is missing.");
        }

        Put(Path.Combine(source, Strings), files.StringsPath);
        Put(Path.Combine(source, GlossaryFile), files.GlossaryPath);
        var scripts = Path.Combine(source, Scripts);
        if (Directory.Exists(scripts) || Directory.Exists(files.AgentScriptsFolder))
        {
            FolderCopy.Replace(scripts, files.AgentScriptsFolder);
        }
    }

    /// <summary>The fingerprint of the job's agent scripts (<c>null</c> when it has none), to tell whether a fix changed them.</summary>
    public static string? ScriptsFingerprint(GenerationFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return FolderCopy.Fingerprint(files.AgentScriptsFolder);
    }

    /// <summary>The string store of a captured state, or <c>null</c> when it had none.</summary>
    /// <exception cref="GenerationFileException">It is unreadable.</exception>
    public static Packs.StringTable? ReadStrings(string source) => new GenerationFiles(source).ReadStrings();

    /// <summary>The glossary of a captured state, or <c>null</c> when it had none.</summary>
    /// <exception cref="GenerationFileException">It is unreadable.</exception>
    public static Glossary? ReadGlossary(string source) => new GenerationFiles(source).ReadGlossary();

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Copy(from, to, overwrite: true);
        }
    }

    private static void Put(string from, string to)
    {
        if (File.Exists(from))
        {
            AtomicFile.WriteAllBytes(to, File.ReadAllBytes(from));
        }
        else
        {
            File.Delete(to);
        }
    }
}
