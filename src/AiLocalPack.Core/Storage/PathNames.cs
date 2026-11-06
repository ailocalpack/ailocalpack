namespace AiLocalPack.Core.Storage;

/// <summary>Comparing folder paths the way the OS does: case-sensitive on Linux only, trailing separators ignored.</summary>
public static class PathNames
{
    /// <summary>Path comparer of the running OS (ordinal; ignores case on Windows and macOS).</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Both paths name the same folder, ignoring trailing separators. <c>null</c> equals nothing.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && Comparer.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b));

    /// <summary>
    /// The absolute path with every symlinked folder on the way resolved (<c>~/.steam/steam</c> → <c>~/.local/share/Steam</c>,
    /// macOS <c>/var</c> → <c>/private/var</c>), as the OS reports a running program's path. Never throws for disk state.
    /// </summary>
    public static string RealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Steam.SteamLocator.RealPath(path, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
