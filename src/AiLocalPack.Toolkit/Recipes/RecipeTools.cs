using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes.Godot;
using AiLocalPack.Toolkit.Recipes.RenPy;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using AiLocalPack.Toolkit.Recipes.Unity;
using AiLocalPack.Toolkit.Recipes.Unreal;

namespace AiLocalPack.Toolkit.Recipes;

/// <summary>
/// The app's recipe tools. A recipe is supported when a tool has its ID, the same major version and at least its
/// minor version (semantic versioning: a minor version adds, a major one breaks), and the game's engine isn't switched
/// off on this OS (<see cref="Disabled"/>, <see cref="DisabledOn"/>). A switched-off engine is still detected (engine
/// chip, fingerprint, version files unchanged), but the toolkit doesn't touch its games: no extract, rebuild, font swap
/// or install; generation treats them like an unknown engine.
/// </summary>
public sealed class RecipeTools : IRecipeTools
{
    /// <summary>
    /// <c>AILOCALPACK_TOOLKIT_ALL_ENGINES=1</c> switches nothing off whatever the OS (development and tests: the RPG Maker
    /// code is still exercised on a Mac).
    /// </summary>
    public const string AllEnginesVariable = "AILOCALPACK_TOOLKIT_ALL_ENGINES";

    /// <param name="disabled">Engines the toolkit leaves alone (a kind without a variant covers every variant).</param>
    public RecipeTools(IEnumerable<IRecipeTool> tools, IEnumerable<EngineKind>? disabled = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        All = [.. tools];
        Disabled = [.. disabled ?? []];
    }

    /// <summary>Every tool this app ships, new instances.</summary>
    public static IReadOnlyList<IRecipeTool> BuiltinTools() => [new RpgMakerRecipe(), new RenPyRecipe(), new GodotRecipe(), new UnityRecipe(), new UnrealRecipe()];

    /// <summary>Every tool this app ships, nothing switched off (any OS; tests and tools that only read).</summary>
    public static RecipeTools Builtin { get; } = new(BuiltinTools());

    /// <summary>
    /// Engines the toolkit leaves alone on <paramref name="os"/> for now. macOS: RPG Maker (MV and MZ), decided
    /// 2026-11-07: their Mac builds don't run on macOS 11+ (old NW.js), so no install can be checked in game there; on a
    /// Mac they are translated like an unknown engine (agent code, task 57). Windows and Linux: none. The code stays
    /// and is tested; emptying the list switches it back on.
    /// </summary>
    public static IReadOnlyList<EngineKind> DisabledOn(OsKind os) => os == OsKind.MacOS ? [new EngineKind(GameEngine.RpgMaker)] : [];

    /// <summary>The tools for this machine: <see cref="DisabledOn"/> its OS, unless <see cref="AllEnginesVariable"/> is <c>1</c>.</summary>
    public static RecipeTools Create(OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return new RecipeTools(BuiltinTools(), env.GetVariable(AllEnginesVariable) == "1" ? [] : DisabledOn(env.Os));
    }

    public IReadOnlyList<IRecipeTool> All { get; }

    /// <summary>Engines switched off (a kind without a variant covers every variant).</summary>
    public IReadOnlyList<EngineKind> Disabled { get; }

    /// <summary>The tool with this ID, or <c>null</c>.</summary>
    public IRecipeTool? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>The tool for games of this engine, or <c>null</c> (no toolkit support yet). Ignores <see cref="Disabled"/>: use <see cref="For(EngineKind)"/> for a game.</summary>
    public IRecipeTool? For(GameEngine engine) => All.FirstOrDefault(t => t.Engine == engine);

    /// <summary>The tool for a game of this engine and variant, or <c>null</c> (no tool, or the engine is switched off).</summary>
    public IRecipeTool? For(EngineKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return IsDisabled(kind) ? null : For(kind.Engine);
    }

    /// <summary>The engine (variant) is one the toolkit leaves alone here (<see cref="Disabled"/>).</summary>
    public bool IsDisabled(EngineKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Disabled.Any(d => d.Engine == kind.Engine && (d.Variant is null || d.Variant == kind.Variant));
    }

    public RecipeToolSupport Check(PackRecipe recipe, string? engine = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (engine is not null && EngineKind.TryParse(engine, out var kind) && IsDisabled(kind) && Find(recipe.Tool)?.Engine == kind.Engine)
        {
            return RecipeToolSupport.EngineUnsupported;
        }

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
