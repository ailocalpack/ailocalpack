namespace AiLocalPack.Toolkit.Engines;

/// <summary>Finding a game's executable among the files of a folder.</summary>
internal static class Executables
{
    /// <summary>Extensions of Linux game binaries (Unity, Godot exports); a Linux binary may also have none.</summary>
    private static readonly string[] LinuxExtensions = [".x86_64", ".x86", ".x86_32", ".arm64", ".arm32"];

    /// <summary>Helpers shipped next to games that are never the game itself (compared lower case).</summary>
    private static readonly string[] HelperNameParts = ["crashhandler", "crashreport", "crashpad", "redist", "prereq", "notification_helper", "dxsetup"];

    /// <summary>Windows executables (minus installers and crash handlers), then Linux binaries; each group sorted by name.</summary>
    public static IEnumerable<string> In(FolderView folder)
    {
        var windows = folder.FileNames.Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsHelper(n));
        var linux = folder.FileNames.Where(n => !IsHelper(n) && IsLinuxBinary(folder, n));
        return windows.Concat(linux).Select(folder.RelativeOf);
    }

    /// <summary>
    /// The executable named <paramref name="stem"/> (<c>Game.exe</c>, <c>Game.x86_64</c>, <c>Game</c> as an ELF binary),
    /// Windows first; <c>null</c> if there is none.
    /// </summary>
    public static string? Named(FolderView folder, string stem) =>
        folder.File($"{stem}.exe")
        ?? LinuxExtensions.Select(e => folder.File(stem + e)).FirstOrDefault(f => f is not null)
        ?? (folder.File(stem) is { } bare && BinaryProbe.IsElf(folder.FullPathOf(Path.GetFileName(bare))) ? bare : null);

    /// <summary><see cref="Named"/>, else the first of <see cref="In"/>.</summary>
    public static string? NamedOrFirst(FolderView folder, string stem) => Named(folder, stem) ?? In(folder).FirstOrDefault();

    public static bool IsLinuxBinary(FolderView folder, string name) =>
        LinuxExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
        || (!Path.HasExtension(name) && BinaryProbe.IsElf(folder.FullPathOf(name)));

    private static bool IsHelper(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("unins", StringComparison.Ordinal) || HelperNameParts.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }
}
