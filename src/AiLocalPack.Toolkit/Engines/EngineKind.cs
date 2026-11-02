using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AiLocalPack.Toolkit.Engines;

/// <summary>Game engines the toolkit knows.</summary>
public enum GameEngine
{
    Unknown,
    Unity,
    Unreal,
    RpgMaker,
    RenPy,
    Godot,
}

/// <summary>How sure a detection is: every marker of the layout found, part of it, or a single weak hint.</summary>
public enum EngineConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>Variant names used by <see cref="EngineKind"/>.</summary>
public static class EngineVariants
{
    /// <summary>Unity with C# assemblies in <c>Managed/</c>.</summary>
    public const string Mono = "mono";

    /// <summary>Unity compiled to native code (<c>GameAssembly</c>, <c>il2cpp_data/</c>).</summary>
    public const string Il2Cpp = "il2cpp";

    public const string RpgMakerMv = "mv";
    public const string RpgMakerMz = "mz";

    /// <summary>Unreal Engine 4.</summary>
    public const string Unreal4 = "4";

    /// <summary>Unreal Engine 5.</summary>
    public const string Unreal5 = "5";
}

/// <summary>
/// Engine plus variant (Unity Mono / IL2CPP, Unreal 4 / 5, RPG Maker MV / MZ, Godot major version; <c>null</c> when not
/// known or not applicable). Its text form is the engine ID stored in the game catalog: <c>unity:il2cpp</c>,
/// <c>unreal:5</c>, <c>rpgmaker:mz</c>, <c>renpy</c>, <c>godot:4</c>, <c>unknown</c>; parse and format round-trip.
/// </summary>
public sealed record EngineKind
{
    private static readonly (GameEngine Engine, string Id)[] Ids =
    [
        (GameEngine.Unknown, "unknown"),
        (GameEngine.Unity, "unity"),
        (GameEngine.Unreal, "unreal"),
        (GameEngine.RpgMaker, "rpgmaker"),
        (GameEngine.RenPy, "renpy"),
        (GameEngine.Godot, "godot"),
    ];

    public EngineKind(GameEngine engine, string? variant = null)
    {
        if (!Enum.IsDefined(engine))
        {
            throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine value.");
        }

        if (variant is not null && !IsValidVariant(engine, variant))
        {
            throw new ArgumentException($"'{variant}' is not a variant of {engine}.", nameof(variant));
        }

        Engine = engine;
        Variant = variant;
    }

    public static EngineKind Unknown { get; } = new(GameEngine.Unknown);

    public GameEngine Engine { get; }

    public string? Variant { get; }

    /// <summary>English name for CLI output and logs, e.g. "Unity (IL2CPP)", "Unreal Engine 5", "RPG Maker MZ".</summary>
    public string DisplayName => (Engine, Variant) switch
    {
        (GameEngine.Unity, EngineVariants.Mono) => "Unity (Mono)",
        (GameEngine.Unity, EngineVariants.Il2Cpp) => "Unity (IL2CPP)",
        (GameEngine.Unity, _) => "Unity",
        (GameEngine.Unreal, { } v) => $"Unreal Engine {v}",
        (GameEngine.Unreal, _) => "Unreal Engine",
        (GameEngine.RpgMaker, { } v) => $"RPG Maker {v.ToUpperInvariant()}",
        (GameEngine.RpgMaker, _) => "RPG Maker",
        (GameEngine.RenPy, _) => "Ren'Py",
        (GameEngine.Godot, { } v) => $"Godot {v}",
        (GameEngine.Godot, _) => "Godot",
        _ => "Unknown",
    };

    public override string ToString()
    {
        var id = Ids.First(i => i.Engine == Engine).Id;
        return Variant is null ? id : $"{id}:{Variant}";
    }

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out EngineKind? kind)
    {
        kind = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var id = colon < 0 ? text : text[..colon];
        var variant = colon < 0 ? null : text[(colon + 1)..];
        var match = Ids.Where(i => i.Id == id).Select(i => (GameEngine?)i.Engine).FirstOrDefault();
        if (match is not { } engine || (variant is not null && !IsValidVariant(engine, variant)))
        {
            return false;
        }

        kind = new EngineKind(engine, variant);
        return true;
    }

    private static bool IsValidVariant(GameEngine engine, string variant) => engine switch
    {
        GameEngine.Unity => variant is EngineVariants.Mono or EngineVariants.Il2Cpp,
        GameEngine.Unreal => variant is EngineVariants.Unreal4 or EngineVariants.Unreal5,
        GameEngine.RpgMaker => variant is EngineVariants.RpgMakerMv or EngineVariants.RpgMakerMz,
        GameEngine.Godot => int.TryParse(variant, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && major > 0
            && variant == major.ToString(CultureInfo.InvariantCulture),
        _ => false,
    };
}
