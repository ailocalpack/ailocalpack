using System.Text.Json;

namespace AiLocalPack.Core.Stores;

/// <summary>Small, never-throwing readers shared by the stores.</summary>
internal static class StoreFiles
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>A JSON file's root (cloned); <c>null</c> when missing, unreadable or not JSON.</summary>
    public static JsonElement? ReadJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream, Options);
            return json.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static string? String(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static bool Flag(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>A store version that fits <c>GameVersion.IsBuildId</c> (spaces and '/' replaced), or <c>null</c>.</summary>
    public static string? Version(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = new string([.. text.Trim().Select(c => c is > ' ' and <= '~' and not '/' ? c : '_')]);
        return cleaned.Length > Games.GameVersion.MaxBuildIdLength ? cleaned[..Games.GameVersion.MaxBuildIdLength] : cleaned;
    }

    /// <summary>
    /// <paramref name="executable"/> relative to <paramref name="folder"/>, '/'-separated; an absolute path inside the
    /// folder is made relative, one outside (or with <c>..</c>) gives <c>null</c>.
    /// </summary>
    public static string? Relative(string folder, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var path = executable.Trim().Trim('"');
        if (Path.IsPathRooted(path) || path.Length > 2 && path[1] == ':')
        {
            var relative = Path.GetRelativePath(folder, path);
            if (Path.IsPathRooted(relative))
            {
                return null;
            }

            path = relative;
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts.Any(p => p is "." or "..") ? null : string.Join('/', parts);
    }

    /// <summary>The folder a game was installed into: the game folder's parent (the folder itself at a drive's root).</summary>
    public static string LibraryOf(string installPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(installPath);
        return Path.GetDirectoryName(trimmed) is { Length: > 0 } parent ? parent : trimmed;
    }

    public static bool Exists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
