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
}
