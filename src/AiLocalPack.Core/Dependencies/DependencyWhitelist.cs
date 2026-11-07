using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Dependencies;

/// <summary>
/// The OS and CPU a game build runs on (<c>windows-x64</c>, <c>linux-x86</c>, <c>macos-arm64</c>…): what picks a
/// dependency's release asset. It is the game's build that counts, not the machine: a Windows build under Proton needs
/// the Windows asset.
/// </summary>
public sealed record DependencyPlatform(OsKind Os, string Arch)
{
    /// <summary>The CPU names a platform may carry.</summary>
    public static IReadOnlyList<string> Arches { get; } = ["x86", "x64", "arm64"];

    public override string ToString() => $"{OsName(Os)}-{Arch}";

    public static bool TryParse(string? text, out DependencyPlatform platform)
    {
        platform = null!;
        var parts = text?.Split('-');
        if (parts is not [var os, var arch] || !Arches.Contains(arch, StringComparer.Ordinal))
        {
            return false;
        }

        OsKind? kind = os switch
        {
            "windows" => OsKind.Windows,
            "linux" => OsKind.Linux,
            "macos" => OsKind.MacOS,
            _ => null,
        };
        if (kind is null)
        {
            return false;
        }

        platform = new DependencyPlatform(kind.Value, arch);
        return true;
    }

    public static string OsName(OsKind os) => os switch
    {
        OsKind.Windows => "windows",
        OsKind.Linux => "linux",
        _ => "macos",
    };
}

/// <summary>One official release file of a dependency version, for one platform.</summary>
/// <param name="Url">The official release asset (https).</param>
/// <param name="Size">Its size in bytes, checked with the hash before it is used.</param>
public sealed record DependencyAsset(DependencyPlatform Platform, Uri Url, long Size, string Sha256)
{
    /// <summary>The file name from the URL (<c>BepInEx_win_x64_5.4.23.2.zip</c>), for the cache.</summary>
    public string FileName
    {
        get
        {
            var name = Uri.UnescapeDataString(Url.Segments[^1]);
            return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? name : "asset.zip";
        }
    }
}

/// <summary>A version of a dependency, with its release assets.</summary>
public sealed record DependencyVersion(string Version, IReadOnlyList<DependencyAsset> Assets);

/// <summary>Where the release archive goes in the game folder.</summary>
/// <param name="From">The folder inside the archive whose content is installed (empty = the archive's root).</param>
/// <param name="Into">The folder in the game it goes into (empty = the game folder itself).</param>
public sealed record DependencyLayout(string From, string Into)
{
    public static DependencyLayout Root { get; } = new(string.Empty, string.Empty);
}

/// <summary>
/// A whitelisted executable component (a mod loader such as BepInEx): only these may be installed for a pack, from the
/// official release named here, checked against its SHA-256.
/// </summary>
/// <param name="Id">What a manifest's <c>dependencies[].id</c> names (<c>bepinex</c>).</param>
/// <param name="Name">Shown to the player (<c>BepInEx</c>).</param>
/// <param name="Homepage">The project's official page (https).</param>
/// <param name="Generated">
/// Files and folders the component makes in the game folder when the game runs (<c>BepInEx/LogOutput.log</c>; a folder
/// ends with <c>/</c>, removed when empty; <c>*</c> / <c>?</c> only in a file's last part inside a folder, <c>BepInEx/cache/*</c>):
/// deleted on uninstall when they weren't there before the install.
/// </param>
public sealed record WhitelistedDependency(
    string Id,
    string Name,
    Uri Homepage,
    DependencyLayout Layout,
    IReadOnlyList<string> Generated,
    IReadOnlyList<DependencyVersion> Versions)
{
    public DependencyVersion? Find(string version) => Versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.Ordinal));
}

/// <summary>The archive to install for a game: <see cref="Pick"/>'s result.</summary>
public sealed record DependencyChoice(WhitelistedDependency Dependency, DependencyVersion Version, DependencyAsset Asset);

/// <summary>
/// The registry's dependency whitelist (<c>policy/dependencies.json</c>, published inside <c>index.json</c>, so it is
/// cached and read offline with the pack list): <c>{schemaVersion: 1, dependencies: [{id, name, homepage, layout?: {from?, into?},
/// generated?: [path], versions: [{version, assets: [{platform, url, size, sha256}]}]}]}</c>.
/// </summary>
public sealed partial class DependencyWhitelist
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Where the registry keeps it.</summary>
    public const string PolicyPath = "policy/dependencies.json";

    public DependencyWhitelist(IReadOnlyList<WhitelistedDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        Dependencies = [.. dependencies.OrderBy(d => d.Id, StringComparer.Ordinal)];
    }

    public static DependencyWhitelist Empty { get; } = new([]);

    /// <summary>Ordered by ID.</summary>
    public IReadOnlyList<WhitelistedDependency> Dependencies { get; }

    public WhitelistedDependency? Find(string id) => Dependencies.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    /// <summary>Whether the manifest's dependency (ID and version) is on the list.</summary>
    public bool Contains(PackDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return Find(dependency.Id)?.Find(dependency.Version) is not null;
    }

    /// <summary>
    /// The asset of <paramref name="dependency"/> for a game build running on <paramref name="os"/> with one of
    /// <paramref name="arches"/> (preferred first), or <c>null</c>: not on the list (<paramref name="known"/> = false), or no
    /// asset for that platform.
    /// </summary>
    public DependencyChoice? Pick(PackDependency dependency, OsKind os, IReadOnlyList<string> arches, out bool known)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        ArgumentNullException.ThrowIfNull(arches);
        var entry = Find(dependency.Id);
        var version = entry?.Find(dependency.Version);
        known = version is not null;
        if (version is null)
        {
            return null;
        }

        foreach (var arch in arches)
        {
            if (version.Assets.FirstOrDefault(a => a.Platform.Os == os && a.Platform.Arch == arch) is { } asset)
            {
                return new DependencyChoice(entry!, version, asset);
            }
        }

        return null;
    }

    /// <summary><c>policy/dependencies.json</c>: the list, or every problem (the whole file is refused).</summary>
    public static (DependencyWhitelist? Whitelist, IReadOnlyList<PackFormatError> Errors) Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        DependencyPolicyFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, RegistryJsonContext.Default.DependencyPolicyFileJson);
        }
        catch (JsonException e)
        {
            return (null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return (null, [new PackFormatError("", "The file holds no list (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, CurrentSchemaVersion) is { } versionError)
        {
            return (null, [versionError]);
        }

        var errors = new PackFormatErrors();
        var list = Read(file.Dependencies, "dependencies", errors);
        return errors.Any ? (null, errors.List) : (list, []);
    }

    /// <summary>Reads the entries; a broken one is left out with its problems in <paramref name="errors"/>.</summary>
    internal static DependencyWhitelist Read(IReadOnlyList<DependencyJson?>? entries, string field, PackFormatErrors errors)
    {
        var result = new List<WhitelistedDependency>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (entries?.Count ?? 0); i++)
        {
            var entryField = $"{field}[{i}]";
            var entryErrors = new PackFormatErrors();
            var dependency = entries![i] is { } json ? new Checker(entryField, entryErrors).Check(json) : null;
            if (entries[i] is null)
            {
                entryErrors.Add(entryField, "is empty.");
            }
            else if (dependency is not null && !ids.Add(dependency.Id))
            {
                entryErrors.Add($"{entryField}.id", $"'{dependency.Id}' is listed twice.");
            }

            if (entryErrors.Any)
            {
                foreach (var error in entryErrors.List)
                {
                    errors.Add(error);
                }
            }
            else
            {
                result.Add(dependency!);
            }
        }

        return new DependencyWhitelist(result);
    }

    /// <summary>The entries as written into <c>index.json</c> (by ID; versions as listed; assets by platform).</summary>
    internal IReadOnlyList<DependencyJson?> ToJson() =>
    [
        .. Dependencies.Select(d => new DependencyJson(
            d.Id,
            d.Name,
            d.Homepage.AbsoluteUri,
            d.Layout == DependencyLayout.Root ? null : new DependencyLayoutJson(NullIfEmpty(d.Layout.From), NullIfEmpty(d.Layout.Into)),
            d.Generated.Count > 0 ? d.Generated : null,
            [.. d.Versions.Select(v => new DependencyVersionJson(
                v.Version,
                [.. v.Assets.OrderBy(a => a.Platform.ToString(), StringComparer.Ordinal)
                    .Select(a => new DependencyAssetJson(a.Platform.ToString(), a.Url.AbsoluteUri, a.Size, a.Sha256))]))])),
    ];

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>A generated path: relative, a folder ends with <c>/</c>, wildcards only in a file's last part.</summary>
    public static bool IsGeneratedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var folder = path.EndsWith('/');
        var trimmed = folder ? path[..^1] : path;
        if (!PackFormat.IsRelativePath(trimmed) || trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        // Wildcards only in a file's last part, and never at the game folder's top (a pattern there could match the game's own files).
        var parts = trimmed.Split('/');
        var wild = parts.Select(p => p.IndexOfAny(['*', '?']) >= 0).ToList();
        return !wild.Take(parts.Length - 1).Any(w => w) && !(wild[^1] && (folder || parts.Length == 1));
    }

    private sealed partial class Checker(string field, PackFormatErrors errors)
    {
        public WhitelistedDependency? Check(DependencyJson json)
        {
            var id = json.Id;
            if (id is null)
            {
                errors.Add($"{field}.id", "is missing.");
            }
            else if (!PackFormat.IsSlug(id))
            {
                errors.Add($"{field}.id", $"'{id}' is not a valid ID (lowercase letters, digits and dashes).");
            }

            if (!PackFormat.IsText(json.Name, 80))
            {
                errors.Add($"{field}.name", json.Name is null ? "is missing." : "is empty or too long.");
            }

            var homepage = Https($"{field}.homepage", json.Homepage);
            var layout = Layout(json.Layout);
            var generated = new List<string>();
            for (var i = 0; i < (json.Generated?.Count ?? 0); i++)
            {
                var path = json.Generated![i];
                if (!IsGeneratedPath(path))
                {
                    errors.Add($"{field}.generated[{i}]", $"'{path}' is not a relative '/'-separated path (a folder ends with '/'; '*' or '?' only in the last part of a file inside a folder).");
                }
                else if (!generated.Contains(path!, StringComparer.Ordinal))
                {
                    generated.Add(path!);
                }
            }

            var versions = Versions(json.Versions);
            return errors.Any
                ? null
                : new WhitelistedDependency(id!, json.Name!, homepage!, layout, generated, versions);
        }

        private DependencyLayout Layout(DependencyLayoutJson? layout)
        {
            string Part(string name, string? value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                if (!PackFormat.IsRelativePath(value) || value.Contains(':', StringComparison.Ordinal))
                {
                    errors.Add($"{field}.layout.{name}", $"'{value}' is not a relative '/'-separated folder.");
                    return string.Empty;
                }

                return value;
            }

            return layout is null ? DependencyLayout.Root : new DependencyLayout(Part("from", layout.From), Part("into", layout.Into));
        }

        private List<DependencyVersion> Versions(IReadOnlyList<DependencyVersionJson?>? list)
        {
            var versions = new List<DependencyVersion>();
            if (list is not { Count: > 0 })
            {
                errors.Add($"{field}.versions", list is null ? "is missing." : "lists no version.");
                return versions;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var versionField = $"{field}.versions[{i}]";
                if (list[i] is not { } json)
                {
                    errors.Add(versionField, "is empty.");
                    continue;
                }

                if (json.Version is not { } version || !VersionPattern().IsMatch(version))
                {
                    errors.Add($"{versionField}.version", json.Version is null ? "is missing." : $"'{json.Version}' is not a version (letters, digits, '.', '-', '+'; up to 64).");
                    continue;
                }

                if (versions.Any(v => v.Version == version))
                {
                    errors.Add($"{versionField}.version", $"'{version}' is listed twice.");
                    continue;
                }

                var assets = Assets(versionField, json.Assets);
                versions.Add(new DependencyVersion(version, assets));
            }

            return versions;
        }

        private List<DependencyAsset> Assets(string versionField, IReadOnlyList<DependencyAssetJson?>? list)
        {
            var assets = new List<DependencyAsset>();
            if (list is not { Count: > 0 })
            {
                errors.Add($"{versionField}.assets", list is null ? "is missing." : "lists no release file.");
                return assets;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var assetField = $"{versionField}.assets[{i}]";
                if (list[i] is not { } json)
                {
                    errors.Add(assetField, "is empty.");
                    continue;
                }

                var ok = true;
                if (!DependencyPlatform.TryParse(json.Platform, out var platform))
                {
                    errors.Add($"{assetField}.platform", json.Platform is null ? "is missing." : $"'{json.Platform}' is not a platform (windows|linux|macos-x86|x64|arm64).");
                    ok = false;
                }
                else if (assets.Any(a => a.Platform == platform))
                {
                    errors.Add($"{assetField}.platform", $"'{platform}' is listed twice.");
                    ok = false;
                }

                var url = Https($"{assetField}.url", json.Url);
                if (json.Size is not > 0)
                {
                    errors.Add($"{assetField}.size", json.Size is null ? "is missing." : $"must be 1 byte or more, not {json.Size}.");
                    ok = false;
                }

                if (!PackFormat.IsSha256(json.Sha256))
                {
                    errors.Add($"{assetField}.sha256", json.Sha256 is null ? "is missing." : $"'{json.Sha256}' is not a SHA-256 (64 lowercase hex).");
                    ok = false;
                }

                if (ok && url is not null)
                {
                    assets.Add(new DependencyAsset(platform, url, json.Size!.Value, json.Sha256!));
                }
            }

            return assets;
        }

        private Uri? Https(string name, string? text)
        {
            if (text is null)
            {
                errors.Add(name, "is missing.");
                return null;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add(name, $"'{text}' is not an https:// URL.");
                return null;
            }

            return url;
        }

        [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+\-]{0,63}$")]
        private static partial Regex VersionPattern();
    }
}
