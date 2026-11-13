using System.Text.Json;
using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Packs;

/// <summary>
/// <c>manifest.json</c> of a pack: one localization of one game into one language, as stored in the registry at
/// <c>games/&lt;game&gt;/&lt;language&gt;/&lt;id&gt;/</c>. Read and written by <see cref="PackManifestSerializer"/>;
/// the JSON Schema is <c>schemas/pack-manifest.schema.json</c>. Exactly one of <see cref="Recipe"/> and
/// <see cref="Patch"/> is set; a recipe pack always has <see cref="Strings"/>.
/// </summary>
/// <param name="Id">Pack ID (lowercase letters, digits, '-'), unique for the game + language; the folder name.</param>
/// <param name="Game">The game the pack is for.</param>
/// <param name="Language">Target language, one of <see cref="Languages.GameLanguages.Tags"/>.</param>
/// <param name="Title">Short name shown in the pack list ("Full translation").</param>
/// <param name="Authors">Credited people; the first is the original author, later ones made revisions.</param>
/// <param name="Revisions">Revision history, oldest first, numbered 1, 2, 3…; the last one is the current revision.</param>
/// <param name="Agent">The AI agent that generated the pack, or <c>null</c> for one made by hand.</param>
/// <param name="Builds">Game builds the pack was made for or tested on, with the original hashes of the files it touches.</param>
/// <param name="Dependencies">Executable components from the registry whitelist.</param>
/// <param name="Fonts">Whitelisted open fonts swapped in at install.</param>
public sealed record PackManifest(
    string Id,
    GameId Game,
    string Language,
    string Title,
    IReadOnlyList<PackAuthor> Authors,
    IReadOnlyList<PackRevision> Revisions,
    PackStatus Status,
    PackAgent? Agent,
    IReadOnlyList<PackBuild> Builds,
    PackRecipe? Recipe,
    PackStringsFile? Strings,
    PackPatch? Patch,
    IReadOnlyList<PackDependency> Dependencies,
    IReadOnlyList<string> Fonts,
    PackCoverage Coverage,
    DateTimeOffset Created,
    DateTimeOffset Updated)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The only license registry content is published under.</summary>
    public const string License = "CC-BY-SA-4.0";

    /// <summary>Whether <paramref name="id"/> is a valid pack ID (lowercase letters, digits, single '-', at most 64).</summary>
    public static bool IsValidId(string? id) => PackFormat.IsSlug(id);

    /// <summary>Current revision number (the last of <see cref="Revisions"/>).</summary>
    public int Revision => Revisions[^1].Number;

    /// <summary>The original author.</summary>
    public PackAuthor Author => Authors[0];

    /// <summary>Every game file the pack touches (relative, '/'-separated), across all builds, in ordinal order.</summary>
    public IReadOnlyList<string> TouchedFiles =>
        [.. Builds.SelectMany(b => b.Files.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

public enum PackStatus
{
    /// <summary>Merged after CI passed; installable, shown with a warning.</summary>
    Alpha,

    /// <summary>Confirmed by independent players or granted by maintainers.</summary>
    Verified,
}

/// <param name="GitHub">GitHub login.</param>
/// <param name="Name">Display name, when different from the login.</param>
public sealed record PackAuthor(string GitHub, string? Name = null)
{
    public string DisplayName => Name ?? GitHub;
}

/// <param name="Number">1, 2, 3…</param>
/// <param name="By">GitHub login of the revision author (listed in <see cref="PackManifest.Authors"/>).</param>
public sealed record PackRevision(int Number, DateTimeOffset Date, string By, string? Notes = null);

/// <param name="Id">Agent ID (<c>claude-code</c>, <c>codex</c>…).</param>
/// <param name="Quality">How carefully it translated (<c>quick</c>, <c>balanced</c>, <c>thorough</c>; the app's
/// <c>GenerationQuality</c>); <c>null</c> for packs from before the quality modes. Kept as text: a value from a newer app
/// reads as is.</param>
public sealed record PackAgent(string Id, string? Version = null, string? Model = null, string? Quality = null);

/// <summary>A game build the pack supports.</summary>
/// <param name="BuildId">Store build ID (Steam games); <c>null</c> for a manually added game.</param>
/// <param name="Files">Touched file (relative, '/'-separated) → its original SHA-256 lowercase hex in this build.</param>
public sealed record PackBuild(string? BuildId, IReadOnlyDictionary<string, string> Files);

/// <param name="Tool">Toolkit tool ID that rebuilds game files from the string table (<c>rpgmaker</c>…).</param>
/// <param name="Version">Tool version, <c>major.minor.patch</c>.</param>
/// <param name="Parameters">Tool-specific settings (a JSON object), or <c>null</c>.</param>
public sealed record PackRecipe(string Tool, string Version, JsonElement? Parameters = null);

/// <param name="Path">Relative to the pack folder, under <c>strings/</c> (<c>strings/strings.json.gz</c>).</param>
/// <param name="Sha256">SHA-256 of the stored (compressed) file.</param>
/// <param name="Size">Stored size in bytes.</param>
public sealed record PackStringsFile(string Path, string Sha256, long Size)
{
    /// <summary>Default <see cref="Path"/>.</summary>
    public const string DefaultPath = "strings/strings.json.gz";
}

/// <summary>Binary patch fallback: applies over the files of the pack's only build.</summary>
/// <param name="Format">Delta format; <c>vcdiff</c>.</param>
/// <param name="Url">Download URL (GitHub Release asset), <c>https</c> only.</param>
/// <param name="Sha256">SHA-256 of the downloaded patch.</param>
/// <param name="Size">Download size in bytes.</param>
/// <param name="Files">Touched file → its SHA-256 after patching.</param>
public sealed record PackPatch(string Format, Uri Url, string Sha256, long Size, IReadOnlyDictionary<string, string> Files)
{
    public const string VcdiffFormat = "vcdiff";
}

/// <param name="Id">Whitelist ID in the registry (<c>bepinex</c>…).</param>
public sealed record PackDependency(string Id, string Version);

/// <summary>How many of the game's strings the pack translates.</summary>
public sealed record PackCoverage(int Translated, int Total)
{
    /// <summary>0…1.</summary>
    public double Ratio => Total == 0 ? 0 : (double)Translated / Total;
}
