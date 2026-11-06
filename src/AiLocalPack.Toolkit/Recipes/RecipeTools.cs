using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes.RpgMaker;

namespace AiLocalPack.Toolkit.Recipes;

/// <summary>
/// The app's recipe tools. A recipe is supported when a tool has its ID, the same major version and at least its
/// minor version (semantic versioning: a minor version adds, a major one breaks).
/// </summary>
public sealed class RecipeTools : IRecipeTools
{
    public RecipeTools(IEnumerable<IRecipeTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        All = [.. tools];
    }

    /// <summary>Every tool this app ships.</summary>
    public static RecipeTools Builtin { get; } = new([new RpgMakerRecipe()]);

    public IReadOnlyList<IRecipeTool> All { get; }

    /// <summary>The tool with this ID, or <c>null</c>.</summary>
    public IRecipeTool? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>The tool for games of this engine, or <c>null</c> (no toolkit support yet).</summary>
    public IRecipeTool? For(GameEngine engine) => All.FirstOrDefault(t => t.Engine == engine);

    public RecipeToolSupport Check(PackRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (Find(recipe.Tool) is not { } tool)
        {
            return RecipeToolSupport.UnknownTool;
        }

        if (!Version.TryParse(recipe.Version, out var needed))
        {
            return RecipeToolSupport.UnknownTool;
        }

        // An older major version is a format this app no longer reads; a newer one or a newer minor needs an app update.
        return needed.Major < tool.Version.Major ? RecipeToolSupport.UnknownTool
            : needed.Major > tool.Version.Major || needed.Minor > tool.Version.Minor ? RecipeToolSupport.TooNew
            : RecipeToolSupport.Supported;
    }
}
