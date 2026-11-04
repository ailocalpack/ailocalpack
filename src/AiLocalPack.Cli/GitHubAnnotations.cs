namespace AiLocalPack.Cli;

/// <summary>
/// GitHub Actions workflow commands (<c>::error file=…,title=…::message</c>): a problem shows on the file in the pull
/// request. Paths are relative to the current folder, which in a workflow is the checkout root.
/// </summary>
internal static class GitHubAnnotations
{
    /// <param name="level"><c>error</c> or <c>warning</c>.</param>
    /// <param name="file">Full path of the file the problem is in, or <c>null</c>.</param>
    /// <param name="title">Short title (the field), or empty.</param>
    public static string Format(string level, string? file, string title, string message)
    {
        var properties = new List<string>();
        if (file is not null)
        {
            properties.Add($"file={Property(Path.GetRelativePath(Environment.CurrentDirectory, file).Replace('\\', '/'))}");
        }

        if (title.Length > 0)
        {
            properties.Add($"title={Property(title)}");
        }

        return $"::{level}{(properties.Count > 0 ? " " + string.Join(",", properties) : "")}::{Data(message)}";
    }

    private static string Data(string text) => text.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    private static string Property(string text) => Data(text)
        .Replace(":", "%3A", StringComparison.Ordinal)
        .Replace(",", "%2C", StringComparison.Ordinal);
}
