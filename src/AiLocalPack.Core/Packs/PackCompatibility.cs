namespace AiLocalPack.Core.Packs;

/// <summary>Whether a pack can be installed on the player's copy of the game.</summary>
public enum PackCompatibilityVerdict
{
    /// <summary>Made for this exact build: installs as tested.</summary>
    Compatible,

    /// <summary>
    /// Some touched files differ from every build the pack lists, but the recipe rebuilds them from the strings of the
    /// player's own files; strings the new build added or renamed stay untranslated.
    /// </summary>
    ProbablyCompatible,

    /// <summary>Can't be installed; <see cref="PackCompatibility.Reason"/> says why.</summary>
    Incompatible,
}

/// <summary>Why a <see cref="PackCompatibilityVerdict"/> was given; the UI and CLI word it from this and the details.</summary>
public enum PackCompatibilityReason
{
    /// <summary>Compatible: the game's store build ID is one the pack lists.</summary>
    BuildListed,

    /// <summary>Compatible: every touched file has the hash one of the pack's builds lists (manual game, or an unlisted build ID).</summary>
    FilesMatch,

    /// <summary>Probably compatible: some touched files changed; the recipe rebuilds them from the strings.</summary>
    FilesChanged,

    /// <summary>Incompatible: the pack is for another game.</summary>
    WrongGame,

    /// <summary>Incompatible: the game folder can't be reached (unplugged drive, folder gone).</summary>
    GameNotAvailable,

    /// <summary>Incompatible: files the pack touches are not in the game folder.</summary>
    FilesMissing,

    /// <summary>Incompatible: a touched file exists but can't be read.</summary>
    FileUnreadable,

    /// <summary>Incompatible: a binary patch applies only to the exact files it was made from, and some changed.</summary>
    PatchFilesChanged,

    /// <summary>Incompatible: this app has no toolkit tool with the recipe's ID.</summary>
    RecipeToolUnknown,

    /// <summary>Incompatible: the recipe needs a newer version of the toolkit tool than this app has.</summary>
    RecipeToolTooNew,
}

/// <summary>
/// Result of <see cref="PackCompatibilityChecker.Check"/>: the verdict, its reason and the data the UI explains it with
/// ("built for build 1234500; you have 1234567; 2 of 5 touched files changed").
/// </summary>
/// <param name="Verdict">Compatible / probably compatible / incompatible.</param>
/// <param name="Reason">Why.</param>
/// <param name="PackBuildIds">Store build IDs the pack lists, in manifest order (empty for a manual game).</param>
/// <param name="GameBuildId">The player's store build ID, or <c>null</c> (manual game, unknown build).</param>
/// <param name="ReferenceBuildId">
/// Build ID of the pack build the files were compared with: the matching one, else the closest one (fewest changed,
/// then fewest missing files; the later build on a tie). <c>null</c> for a manual game or when no files were compared.
/// </param>
/// <param name="TouchedFileCount">Files the reference build touches (all touched files when there is no reference build).</param>
/// <param name="ChangedFiles">Touched files whose hash differs from the reference build, ordinal order.</param>
/// <param name="MissingFiles">Touched files not in the game folder, ordinal order.</param>
/// <param name="UnreadableFile">The file that couldn't be read (<see cref="PackCompatibilityReason.FileUnreadable"/>).</param>
/// <param name="RecipeTool">Recipe tool and version (<see cref="PackCompatibilityReason.RecipeToolUnknown"/> / <see cref="PackCompatibilityReason.RecipeToolTooNew"/>).</param>
public sealed record PackCompatibility(
    PackCompatibilityVerdict Verdict,
    PackCompatibilityReason Reason,
    IReadOnlyList<string> PackBuildIds,
    string? GameBuildId,
    string? ReferenceBuildId,
    int TouchedFileCount,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> MissingFiles,
    string? UnreadableFile = null,
    PackRecipe? RecipeTool = null)
{
    /// <summary>Compatible or probably compatible.</summary>
    public bool CanInstall => Verdict != PackCompatibilityVerdict.Incompatible;
}

/// <summary>What this app's toolkit says about a recipe.</summary>
public enum RecipeToolSupport
{
    Supported,

    /// <summary>No tool with this ID.</summary>
    UnknownTool,

    /// <summary>The tool exists in an older major version, or an older minor version than the recipe needs.</summary>
    TooNew,
}

/// <summary>The toolkit's recipe tools as Core sees them (implemented in Toolkit from task 28).</summary>
public interface IRecipeTools
{
    RecipeToolSupport Check(PackRecipe recipe);
}
