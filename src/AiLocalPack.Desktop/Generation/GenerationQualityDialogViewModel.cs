using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.GamePage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Generation;

/// <summary>A column of the compare dialog: one quality, its cells top to bottom, and <c>Choose</c>.</summary>
public sealed partial class GenerationQualityColumn(GenerationQuality quality, bool isCurrent, IReadOnlyList<string> cells, string name, Action<GenerationQuality> choose)
    : ObservableObject
{
    public GenerationQuality Quality { get; } = quality;

    public string Name { get; } = name;

    public string Icon { get; } = GenerationQualityTexts.Icon(quality);

    public bool IsCurrent { get; } = isCurrent;

    public IReadOnlyList<string> Cells { get; } = cells;

    [RelayCommand]
    private void Choose() => choose(Quality);
}

/// <summary>What the compare dialog shows: the game, its size estimate, the agent and the chosen quality.</summary>
/// <param name="Estimate">The Balanced estimate of the game (others derived), or <c>null</c> while estimating.</param>
/// <param name="OnChosen">Runs with the quality the player chose (not after Close / Esc).</param>
public sealed record GenerationQualityCompareRequest(
    string GameName,
    string? AgentId,
    string AgentName,
    GenerationQuality Current,
    GenerationEstimate? Estimate,
    Func<GenerationEstimate, string> Time,
    Func<GenerationEstimate, string> Limits,
    Action<GenerationQuality> OnChosen);

/// <summary>
/// "Compare translation modes" (the start form's popover, design §6a): one column per quality with its model, whether it
/// studies the game first, web lookup, what each chunk gets, agent time, subscription and what it is best for, and
/// <c>Choose</c>. Esc / Close keeps the choice.
/// </summary>
public sealed partial class GenerationQualityDialogViewModel : DialogViewModel
{
    private readonly IDialogService _dialogs;
    private readonly GenerationQualityCompareRequest _request;

    public GenerationQualityDialogViewModel(GenerationQualityCompareRequest request, IDialogService dialogs, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(localizer);
        _request = request;
        _dialogs = dialogs;
        Title = localizer[nameof(Strings.Gen_Quality_Compare_Title)];
        Subtitle = request.Estimate is { } e
            ? localizer.Format(nameof(Strings.Gen_Quality_Compare_Subtitle), request.GameName, e.Text.Strings.ToString("N0", localizer.Culture), request.AgentName)
            : localizer.Format(nameof(Strings.Gen_Quality_Compare_SubtitleNoSize), request.GameName, request.AgentName);
        RowNames =
        [
            localizer[nameof(Strings.Gen_Quality_Row_Model)],
            localizer[nameof(Strings.Gen_Quality_Row_Study)],
            localizer[nameof(Strings.Gen_Quality_Row_Web)],
            localizer[nameof(Strings.Gen_Quality_Row_Chunk)],
            localizer[nameof(Strings.Gen_Quality_Row_Time)],
            localizer[nameof(Strings.Gen_Quality_Row_Limits)],
            localizer[nameof(Strings.Gen_Quality_Row_BestFor)],
        ];
        Columns = [.. GenerationQualityTexts.All.Select(q => new GenerationQualityColumn(
            q,
            q == request.Current,
            Cells(q, request, localizer),
            GenerationQualityTexts.Name(q, localizer),
            Choose))];
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<string> RowNames { get; }

    public IReadOnlyList<GenerationQualityColumn> Columns { get; }

    [RelayCommand]
    private void Close() => _dialogs.Close(this);

    private void Choose(GenerationQuality quality)
    {
        _dialogs.Close(this);
        _request.OnChosen(quality);
    }

    private static string[] Cells(GenerationQuality q, GenerationQualityCompareRequest request, Localizer localizer)
    {
        var thorough = q == GenerationQuality.Thorough;
        var estimate = request.Estimate?.At(q);
        return
        [
            GenerationQualityTexts.Model(q, request.AgentId, localizer),
            localizer[thorough ? nameof(Strings.Gen_Quality_Cell_StudyYes) : nameof(Strings.Gen_Quality_Cell_No)],
            localizer[thorough ? nameof(Strings.Gen_Quality_Cell_WebYes) : nameof(Strings.Gen_Quality_Cell_No)],
            localizer[thorough ? nameof(Strings.Gen_Quality_Cell_ChunkBrief) : nameof(Strings.Gen_Quality_Cell_ChunkGlossary)],
            estimate is null ? "—" : request.Time(estimate),
            estimate is null ? "—" : request.Limits(estimate),
            localizer[q switch
            {
                GenerationQuality.Quick => nameof(Strings.Gen_Quality_Quick_BestFor),
                GenerationQuality.Thorough => nameof(Strings.Gen_Quality_Thorough_BestFor),
                _ => nameof(Strings.Gen_Quality_Balanced_BestFor),
            }],
        ];
    }
}
