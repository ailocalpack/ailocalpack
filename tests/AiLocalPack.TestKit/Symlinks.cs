namespace AiLocalPack.TestKit;

/// <summary>
/// Whether this machine lets a test create symbolic links: Windows allows it only with admin rights or developer
/// mode (CI runners have it, most players' machines don't). Use as
/// <c>[Fact(SkipType = typeof(Symlinks), SkipWhen = nameof(Symlinks.Unavailable), Skip = Symlinks.Reason)]</c>.
/// </summary>
public static class Symlinks
{
    public const string Reason = "This machine doesn't allow creating symbolic links (Windows without admin rights or developer mode)";

    private static readonly Lazy<bool> Probe = new(CanCreate);

    /// <summary><c>true</c> when creating a symbolic link fails here.</summary>
    public static bool Unavailable => !Probe.Value;

    private static bool CanCreate()
    {
        using var dir = new TempDir();
        try
        {
            Directory.CreateDirectory(dir.Combine("target"));
            File.WriteAllText(dir.Combine("target.txt"), "");
            Directory.CreateSymbolicLink(dir.Combine("folder-link"), dir.Combine("target"));
            File.CreateSymbolicLink(dir.Combine("file-link"), dir.Combine("target.txt"));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
