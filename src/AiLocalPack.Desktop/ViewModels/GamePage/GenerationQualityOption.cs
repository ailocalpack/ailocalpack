using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>Words and icons of the translation qualities (the start form's mode button, its popover, the compare dialog, the progress chip).</summary>
public static class GenerationQualityTexts
{
    public static IReadOnlyList<GenerationQuality> All { get; } = [GenerationQuality.Quick, GenerationQuality.Balanced, GenerationQuality.Thorough];

    /// <summary>"Quick", "Balanced", "Thorough".</summary>
    public static string Name(GenerationQuality quality, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[quality switch
        {
            GenerationQuality.Quick => nameof(Strings.Gen_Quality_Quick),
            GenerationQuality.Thorough => nameof(Strings.Gen_Quality_Thorough),
            _ => nameof(Strings.Gen_Quality_Balanced),
        }];
    }

    /// <summary>One line on what the quality does.</summary>
    public static string Blurb(GenerationQuality quality, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[quality switch
        {
            GenerationQuality.Quick => nameof(Strings.Gen_Quality_Quick_Blurb),
            GenerationQuality.Thorough => nameof(Strings.Gen_Quality_Thorough_Blurb),
            _ => nameof(Strings.Gen_Quality_Balanced_Blurb),
        }];
    }

    /// <summary>Resource key of the quality's icon.</summary>
    public static string Icon(GenerationQuality quality) => quality switch
    {
        GenerationQuality.Quick => "IconZap",
        GenerationQuality.Thorough => "IconGem",
        _ => "IconScale",
    };

    /// <summary>
    /// The model the quality runs with this agent: Claude Code's family ("Opus"), Codex's default model with the effort
    /// ("default model, high effort"), else "the agent's default model".
    /// </summary>
    public static string Model(GenerationQuality quality, string? agentId, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var choice = AgentModels.For(agentId ?? string.Empty, quality);
        if (choice.Model is { Length: > 0 } model)
        {
            return char.ToUpperInvariant(model[0]) + model[1..];
        }

        return choice.Effort switch
        {
            AgentEffort.Low => localizer[nameof(Strings.Gen_Quality_Model_EffortLow)],
            AgentEffort.Medium => localizer[nameof(Strings.Gen_Quality_Model_EffortMedium)],
            AgentEffort.High => localizer[nameof(Strings.Gen_Quality_Model_EffortHigh)],
            _ => localizer[nameof(Strings.Gen_Quality_Model_Default)],
        };
    }

    /// <summary>"Translates with Sonnet" / "Studies the game and translates with Opus".</summary>
    public static string ModelLine(GenerationQuality quality, string? agentId, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format(
            quality == GenerationQuality.Thorough ? nameof(Strings.Gen_Quality_ModelLine_Study) : nameof(Strings.Gen_Quality_ModelLine),
            Model(quality, agentId, localizer));
    }

    /// <summary>"Thorough · Opus": the progress card's chip.</summary>
    public static string Chip(GenerationQuality quality, string? agentId, Localizer localizer) =>
        $"{Name(quality, localizer)} · {Model(quality, agentId, localizer)}";
}

/// <summary>A row of the mode popover: icon, name (+ "default"), what it does, the model, the time at this game's size and the quality meter.</summary>
public sealed partial class GenerationQualityOption(GenerationQuality quality, Action<GenerationQuality> pick) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _blurb = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _time = string.Empty;

    public GenerationQuality Quality { get; } = quality;

    public string Icon { get; } = GenerationQualityTexts.Icon(quality);

    public bool IsDefault => Quality == GenerationQuality.Balanced;

    /// <summary>The 3-step meter: 1 (Quick) to 3 (Thorough).</summary>
    public int Level => (int)Quality + 1;

    public bool HasSecondBar => Level >= 2;

    public bool HasThirdBar => Level >= 3;

    [RelayCommand]
    private void Select() => pick(Quality);
}
