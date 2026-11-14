using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>
/// A row of the start form's reader popover ("… into Русский with [Unity toolkit ▾]"): one toolkit engine, or the
/// agent's own code (<see cref="GenerationOptions.AgentReader"/>). The detected engine's row is marked; an engine the
/// toolkit leaves alone on this OS can't be picked.
/// </summary>
public sealed partial class GenerationReaderOption : ObservableObject
{
    private readonly Action<string> _pick;

    public GenerationReaderOption(string id, GameEngine? engine, bool isAvailable, Action<string> pick)
    {
        Id = id;
        Engine = engine;
        IsAvailable = isAvailable;
        _pick = pick;
        BrandKey = GenerationReaderTexts.BrandKey(engine);
    }

    /// <summary>A recipe tool's ID, or <see cref="GenerationOptions.AgentReader"/>.</summary>
    public string Id { get; }

    /// <summary>The tool's engine; <c>null</c> for the agent's own code.</summary>
    public GameEngine? Engine { get; }

    public bool IsAgent => Engine is null;

    /// <summary>The toolkit handles this engine on this OS.</summary>
    public bool IsAvailable { get; }

    /// <summary>The engine's mark (<c>Styles/BrandIcons.axaml</c>), or <c>null</c> (the agent; RPG Maker, which has none).</summary>
    public string? BrandKey { get; }

    public bool HasBrand => BrandKey is not null;

    /// <summary>The generic icon when there's no brand mark: the bot for the agent, the box for an engine.</summary>
    public string Icon => IsAgent ? "IconBot" : "IconBox";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDetected;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _blurb = string.Empty;

    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private void Select() => _pick(Id);
}

/// <summary>Words and marks of the start form's reader choice.</summary>
public static class GenerationReaderTexts
{
    /// <summary>One row per toolkit tool (by engine name), then the agent's own code.</summary>
    public static IReadOnlyList<GenerationReaderOption> Options(RecipeTools tools, Action<string> pick)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return
        [
            .. tools.All
                .OrderBy(t => new EngineKind(t.Engine).DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(t => new GenerationReaderOption(t.Id, t.Engine, !tools.IsDisabled(new EngineKind(t.Engine)), pick)),
            new GenerationReaderOption(GenerationOptions.AgentReader, null, true, pick),
        ];
    }

    /// <summary>"Unity toolkit" / "AI's own code" (the sentence's button and the popover's rows).</summary>
    public static string Name(GameEngine? engine, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return engine is { } e
            ? localizer.Format(nameof(Strings.Gen_Reader_Toolkit), new EngineKind(e).DisplayName)
            : localizer[nameof(Strings.Gen_Reader_Agent)];
    }

    /// <summary>What the row's choice means for the job and the pack.</summary>
    public static string Blurb(GenerationReaderOption option, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(localizer);
        return option.IsAgent ? localizer[nameof(Strings.Gen_Reader_Agent_Blurb)]
            : !option.IsAvailable ? localizer[nameof(Strings.Gen_Reader_Unavailable)]
            : localizer[nameof(Strings.Gen_Reader_Toolkit_Blurb)];
    }

    public static string? BrandKey(GameEngine? engine) => engine switch
    {
        GameEngine.Unity => "BrandUnity",
        GameEngine.Unreal => "BrandUnreal",
        GameEngine.Godot => "BrandGodot",
        GameEngine.RenPy => "BrandRenPy",
        _ => null,
    };
}
