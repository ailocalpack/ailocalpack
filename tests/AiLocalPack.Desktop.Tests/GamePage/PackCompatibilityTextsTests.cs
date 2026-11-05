using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.GamePage;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The Packs tab's explanation of a compatibility verdict (design §4).</summary>
public sealed class PackCompatibilityTextsTests
{
    private static Localizer L => Localizer.Current;

    private static PackCompatibility Result(
        PackCompatibilityVerdict verdict,
        PackCompatibilityReason reason,
        string? gameBuild = null,
        string? referenceBuild = null,
        int touched = 4,
        IReadOnlyList<string>? changed = null,
        PackRecipe? recipe = null) =>
        new(verdict, reason, referenceBuild is null ? [] : [referenceBuild], gameBuild, referenceBuild, touched, changed ?? [], [], RecipeTool: recipe);

    [Fact]
    public void Matching_files_say_how_many_were_compared()
    {
        // Arrange
        var result = Result(PackCompatibilityVerdict.Compatible, PackCompatibilityReason.FilesMatch);

        // Act
        var text = PackCompatibilityTexts.Explain(result, L);

        // Assert
        text.Should().Be("All files it changes (4) match your copy.");
    }

    [Fact]
    public void Changed_files_of_a_manual_game_name_no_builds()
    {
        // Arrange
        var result = Result(PackCompatibilityVerdict.ProbablyCompatible, PackCompatibilityReason.FilesChanged, changed: ["a.json"]);

        // Act
        var text = PackCompatibilityTexts.Explain(result, L);

        // Assert
        text.Should().Be("Changed files since the version it was made for: 1 of 4; strings the update added stay untranslated.");
    }

    [Fact]
    public void Changed_files_of_a_steam_game_group_the_build_digits()
    {
        // Arrange
        var result = Result(PackCompatibilityVerdict.ProbablyCompatible, PackCompatibilityReason.FilesChanged, "14882031", "14880112", 41, ["a", "b"]);

        // Act
        var text = PackCompatibilityTexts.Explain(result, L);

        // Assert
        text.Should().Be("Made for build 14,880,112, you have 14,882,031. Changed files: 2 of 41; strings the update added stay untranslated.");
    }

    [Fact]
    public void Patch_needs_the_exact_files()
    {
        // Arrange
        var result = Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.PatchFilesChanged, changed: ["a"]);

        // Act
        var text = PackCompatibilityTexts.Explain(result, L);

        // Assert
        text.Should().Be("A binary patch needs the exact files it was made from; changed files: 1 of 4.");
    }

    [Fact]
    public void Too_new_recipe_asks_for_an_app_update()
    {
        // Arrange
        var result = Result(PackCompatibilityVerdict.Incompatible, PackCompatibilityReason.RecipeToolTooNew, recipe: new PackRecipe("unity", "2.1.0"));

        // Act
        var text = PackCompatibilityTexts.Explain(result, L);

        // Assert
        text.Should().Be("It needs «unity» 2.1.0, newer than this app has. Update the app.");
    }

    [Fact]
    public void Server_error_reads_as_no_answer()
    {
        // Arrange
        var problem = PackManifestProblem.HttpError;

        // Act
        var text = PackCompatibilityTexts.Failure(problem, L);

        // Assert
        text.Should().Be("The registry didn't answer. Check the connection and retry.");
    }
}
