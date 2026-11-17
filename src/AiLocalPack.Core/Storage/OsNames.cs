namespace AiLocalPack.Core.Storage;

/// <summary>
/// The canonical names of <see cref="OsKind"/> in files: <c>windows</c>, <c>macos</c>, <c>linux</c> (dependency
/// platforms, a pack build's <c>os</c>, the index's <c>buildOs</c>, the game catalog's <c>platform</c>).
/// </summary>
public static class OsNames
{
    public static string Name(OsKind os) => os switch
    {
        OsKind.Windows => "windows",
        OsKind.Linux => "linux",
        _ => "macos",
    };

    /// <summary>Exactly a canonical name (lowercase); anything else is <c>false</c>.</summary>
    public static bool TryParse(string? text, out OsKind os)
    {
        (var ok, os) = text switch
        {
            "windows" => (true, OsKind.Windows),
            "linux" => (true, OsKind.Linux),
            "macos" => (true, OsKind.MacOS),
            _ => (false, default),
        };
        return ok;
    }
}
