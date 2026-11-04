using System.Diagnostics.CodeAnalysis;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Registry;

/// <summary>
/// Where the registry index is read from: the public one on GitHub Pages, or a dev override (<c>AILOCALPACK_INDEX_URL</c>,
/// else the hidden setting <see cref="Settings.AppSettings.RegistryIndexUrl"/>) that names another <c>https://</c> URL or
/// a local folder, so every flow can be checked against a local registry. Pack files are resolved relative to
/// <see cref="IndexUri"/>.
/// </summary>
public sealed record RegistryLocation
{
    /// <summary>Replaces the registry: an <c>https://</c> URL or a local folder path (dev, tests).</summary>
    public const string IndexUrlVariable = "AILOCALPACK_INDEX_URL";

    public const string IndexFileName = "index.json";

    /// <summary>Baked into the app; must never move (see CLAUDE.md → Repositories).</summary>
    public static Uri DefaultIndexUri { get; } = new("https://ailocalpack.github.io/registry/index.json");

    public static RegistryLocation Default { get; } = new(DefaultIndexUri);

    private RegistryLocation(Uri indexUri) => IndexUri = indexUri;

    /// <summary><c>https://…/index.json</c> or <c>file:///…/index.json</c>.</summary>
    public Uri IndexUri { get; }

    /// <summary>A folder on this machine: read directly, no download and no cache.</summary>
    public bool IsLocal => IndexUri.IsFile;

    public bool IsDefault => IndexUri == DefaultIndexUri;

    /// <summary>Path of the local <c>index.json</c>, or <c>null</c> for a URL.</summary>
    public string? LocalIndexPath => IsLocal ? IndexUri.LocalPath : null;

    /// <summary>
    /// Parses an override: an <c>https://</c> URL (of <c>index.json</c> or of the folder holding it), a <c>file://</c>
    /// URL, or a local path (absolute, or relative to <paramref name="baseDirectory"/>, by default the current one) of
    /// a registry folder or its <c>index.json</c>. Plain <c>http://</c> and other schemes are refused.
    /// </summary>
    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out RegistryLocation? location,
        [NotNullWhen(false)] out string? error,
        string? baseDirectory = null)
    {
        location = null;
        error = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "The registry address is empty.";
            return false;
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                error = $"'{value}' is not a valid URL.";
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                location = new RegistryLocation(ToIndexUri(uri));
                return true;
            }

            if (uri.IsFile)
            {
                return TryLocal(uri.LocalPath, out location, out error);
            }

            error = $"'{value}' must be an https:// URL or a local folder.";
            return false;
        }

        try
        {
            var path = baseDirectory is null ? Path.GetFullPath(value) : Path.GetFullPath(value, baseDirectory);
            return TryLocal(path, out location, out error);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{value}' is not a valid folder path.";
            return false;
        }
    }

    /// <summary>
    /// <c>AILOCALPACK_INDEX_URL</c>, else <paramref name="setting"/>, else <see cref="Default"/>. An override that
    /// doesn't parse is an error (not a silent fall back to the public registry).
    /// </summary>
    public static bool TryResolve(
        OsEnvironment env,
        string? setting,
        [NotNullWhen(true)] out RegistryLocation? location,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.GetVariable(IndexUrlVariable) is { } fromEnv)
        {
            if (TryParse(fromEnv, out location, out error))
            {
                return true;
            }

            error = $"{IndexUrlVariable}: {error}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(setting))
        {
            if (TryParse(setting, out location, out error))
            {
                return true;
            }

            error = $"registryIndexUrl setting: {error}";
            return false;
        }

        location = Default;
        error = null;
        return true;
    }

    /// <summary>A registry file named relative to <c>index.json</c> (a pack's manifest).</summary>
    public Uri Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return new Uri(IndexUri, relativePath);
    }

    public override string ToString() => LocalIndexPath ?? IndexUri.AbsoluteUri;

    private static bool TryLocal(string path, [NotNullWhen(true)] out RegistryLocation? location, [NotNullWhen(false)] out string? error)
    {
        error = null;
        var full = Path.GetFullPath(path);
        var file = File.Exists(full) || full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, IndexFileName);
        location = new RegistryLocation(new Uri(file));
        return true;
    }

    /// <summary><c>…/index.json</c> as is; a folder URL gets <c>index.json</c> appended.</summary>
    private static Uri ToIndexUri(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        builder.Path = builder.Path.TrimEnd('/') + "/" + IndexFileName;
        return builder.Uri;
    }
}

/// <summary>Folder layout of the registry repo: <c>games/&lt;game folder&gt;/&lt;language&gt;/&lt;pack id&gt;/</c>.</summary>
public static class RegistryLayout
{
    public const string GamesFolder = "games";

    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// The folder name of a game: its ID with <c>:</c> → <c>-</c> (<c>steam-620</c>, <c>fp-&lt;hex&gt;</c>), since
    /// Windows doesn't allow <c>:</c> in file names and the registry must be cloneable everywhere.
    /// </summary>
    public static string GameFolder(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.ToString().Replace(':', '-');
    }

    /// <summary>The game a folder name stands for, or <c>null</c>.</summary>
    public static GameId? GameOfFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var dash = folder.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && GameId.TryParse(string.Concat(folder.AsSpan(0, dash), ":", folder.AsSpan(dash + 1)), out var id) ? id : null;
    }

    /// <summary><c>games/steam-620/ru/full/manifest.json</c>.</summary>
    public static string ManifestPath(GameId game, string language, string packId) =>
        $"{GamesFolder}/{GameFolder(game)}/{language}/{packId}/{ManifestFileName}";
}
