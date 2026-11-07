using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Fonts;

/// <summary>A face of a whitelisted font.</summary>
public enum FontFace
{
    Regular,
    Bold,
}

/// <summary>One file of a whitelisted font.</summary>
/// <param name="FileName">File name (no folders), also the name it gets in a game.</param>
/// <param name="Url">Where it is downloaded from (https); <c>null</c> = shipped inside the app.</param>
public sealed record WhitelistFontFile(FontFace Face, string FileName, long Size, string Sha256, Uri? Url)
{
    public bool IsBundled => Url is null;
}

/// <summary>
/// An open (OFL) font the toolkit may put into a game whose own font lacks the letters of a translation.
/// </summary>
/// <param name="Id">Stable ID (a slug), the one a pack manifest's <c>fonts</c> lists.</param>
/// <param name="Name">Family name (not translated).</param>
/// <param name="Languages">Language tags it is made for (<see cref="GameLanguages.Tags"/>); empty for the default font.</param>
/// <param name="IsDefault">The font for every language no other font names (Latin, Cyrillic, Greek, Vietnamese).</param>
public sealed record WhitelistFont(
    string Id,
    string Name,
    string License,
    IReadOnlyList<string> Languages,
    bool IsDefault,
    IReadOnlyList<WhitelistFontFile> Files)
{
    /// <summary>Every file ships inside the app (nothing to download).</summary>
    public bool IsBundled => Files.All(f => f.IsBundled);

    /// <summary>The file of <paramref name="face"/>, or <c>null</c>.</summary>
    public WhitelistFontFile? File(FontFace face) => Files.FirstOrDefault(f => f.Face == face);
}

/// <summary>
/// The open fonts the toolkit may swap into games (task 31): the list bundled with the app (<see cref="Bundled"/>,
/// <c>Fonts/fonts.json</c>). Packs reference these by ID and never ship fonts. Small fonts (Noto Sans for Latin,
/// Cyrillic, Greek and Vietnamese; Noto Sans Arabic; Noto Sans Thai) ship inside the app; the CJK ones (4.5–8.5 MB
/// each) are downloaded from a pinned commit of the official Noto repository when a pack needs them, checked against
/// the size and SHA-256 listed here (<see cref="FontStore"/>).
/// </summary>
public sealed partial class FontWhitelist
{
    public const int CurrentSchemaVersion = 1;

    private static readonly Lazy<FontWhitelist> BundledList = new(LoadBundled);

    private readonly Dictionary<string, WhitelistFont> _byId;

    public FontWhitelist(IReadOnlyList<WhitelistFont> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        Fonts = fonts;
        _byId = fonts.ToDictionary(f => f.Id, StringComparer.Ordinal);
    }

    /// <summary>The list shipped inside the app.</summary>
    public static FontWhitelist Bundled => BundledList.Value;

    /// <summary>Every font, in file order.</summary>
    public IReadOnlyList<WhitelistFont> Fonts { get; }

    public WhitelistFont? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The fonts to try for text in <paramref name="language"/>, best first: the fonts made for the language, then the
    /// default font.
    /// </summary>
    public IReadOnlyList<WhitelistFont> For(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return [
            .. Fonts.Where(f => f.Languages.Contains(language, StringComparer.OrdinalIgnoreCase)),
            .. Fonts.Where(f => f.IsDefault && !f.Languages.Contains(language, StringComparer.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>Reads a whitelist file. Throws <see cref="FontWhitelistException"/> when it isn't valid.</summary>
    public static FontWhitelist Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FontWhitelistFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, FontWhitelistJsonContext.Default.FontWhitelistFile);
        }
        catch (JsonException e)
        {
            throw new FontWhitelistException($"Invalid JSON: {e.Message}", e);
        }

        if (file is null)
        {
            throw new FontWhitelistException("The file is empty.");
        }

        if (file.SchemaVersion != CurrentSchemaVersion)
        {
            throw new FontWhitelistException($"Unsupported schemaVersion {file.SchemaVersion}.");
        }

        var fonts = new List<WhitelistFont>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Fonts ?? [])
        {
            var font = ReadFont(entry);
            if (fonts.Any(f => f.Id == font.Id))
            {
                throw new FontWhitelistException($"Font '{font.Id}' is listed twice.");
            }

            foreach (var item in font.Files)
            {
                if (!files.Add(item.FileName))
                {
                    throw new FontWhitelistException($"Font '{font.Id}': the file name {item.FileName} is used twice.");
                }
            }

            fonts.Add(font);
        }

        if (fonts.Count(f => f.IsDefault) != 1)
        {
            throw new FontWhitelistException("Exactly one font must be the default.");
        }

        return new FontWhitelist(fonts);
    }

    private static WhitelistFont ReadFont(FontJson? entry)
    {
        if (entry?.Id is not { } id || !IdPattern().IsMatch(id))
        {
            throw new FontWhitelistException($"Invalid font ID '{entry?.Id}' (lowercase letters, digits, '-').");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new FontWhitelistException($"Font '{id}' has no name.");
        }

        if (entry.License != "OFL-1.1")
        {
            throw new FontWhitelistException($"Font '{id}': only OFL-1.1 fonts may be listed, not '{entry.License}'.");
        }

        var languages = entry.Languages ?? [];
        foreach (var language in languages)
        {
            if (language is null || !GameLanguages.Tags.Contains(language, StringComparer.Ordinal))
            {
                throw new FontWhitelistException($"Font '{id}': unknown language '{language}'.");
            }
        }

        var files = new List<WhitelistFontFile>();
        foreach (var (faceName, fileEntry) in entry.Files ?? [])
        {
            if (!Enum.TryParse<FontFace>(faceName, ignoreCase: true, out var face) || !string.Equals(faceName, faceName.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new FontWhitelistException($"Font '{id}': unknown face '{faceName}' (regular, bold).");
            }

            files.Add(ReadFile(id, face, fileEntry));
        }

        if (!files.Any(f => f.Face == FontFace.Regular))
        {
            throw new FontWhitelistException($"Font '{id}' has no regular file.");
        }

        return new WhitelistFont(id, entry.Name, entry.License, [.. languages.OfType<string>()], entry.Default, [.. files.OrderBy(f => f.Face)]);
    }

    private static WhitelistFontFile ReadFile(string id, FontFace face, FontFileJson? entry)
    {
        if (entry?.File is not { } name || !FileNamePattern().IsMatch(name))
        {
            throw new FontWhitelistException($"Font '{id}' ({face}): invalid file name '{entry?.File}' (a .ttf or .otf name without folders).");
        }

        if (entry.Size <= 0)
        {
            throw new FontWhitelistException($"Font '{id}' ({face}): invalid size {entry.Size}.");
        }

        if (entry.Sha256 is not { } sha256 || !HashPattern().IsMatch(sha256))
        {
            throw new FontWhitelistException($"Font '{id}' ({face}): invalid sha256 '{entry.Sha256}'.");
        }

        Uri? url = null;
        if (entry.Url is { } text && (!Uri.TryCreate(text, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps))
        {
            throw new FontWhitelistException($"Font '{id}' ({face}): the address '{text}' is not https.");
        }

        return new WhitelistFontFile(face, name, entry.Size, sha256, url);
    }

    private static FontWhitelist LoadBundled()
    {
        using var stream = typeof(FontWhitelist).Assembly.GetManifestResourceStream(ResourceName("fonts.json"))
            ?? throw new InvalidOperationException("The bundled font whitelist is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The manifest resource name of a file shipped in <c>Fonts/</c> (<c>fonts.json</c>, bundled font files).</summary>
    internal static string ResourceName(string fileName) => $"AiLocalPack.Core.Fonts.{fileName}";

    /// <summary>The bundled bytes of <paramref name="file"/>, or <c>null</c> when the app doesn't ship it.</summary>
    internal static Stream? OpenBundled(WhitelistFontFile file) =>
        file.IsBundled ? Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName("Files." + file.FileName)) : null;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\.(ttf|otf)$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();
}

/// <summary>The font whitelist file is not valid.</summary>
public sealed class FontWhitelistException : Exception
{
    public FontWhitelistException()
    {
    }

    public FontWhitelistException(string message)
        : base(message)
    {
    }

    public FontWhitelistException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal sealed record FontWhitelistFile(int SchemaVersion, IReadOnlyList<FontJson?>? Fonts);

internal sealed record FontJson(
    string? Id,
    string? Name,
    string? License,
    bool Default,
    IReadOnlyList<string?>? Languages,
    Dictionary<string, FontFileJson?>? Files);

internal sealed record FontFileJson(string? File, long Size, string? Sha256, string? Url);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FontWhitelistFile))]
internal sealed partial class FontWhitelistJsonContext : JsonSerializerContext;
