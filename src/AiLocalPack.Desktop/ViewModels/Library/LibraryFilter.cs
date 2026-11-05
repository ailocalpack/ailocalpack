using AiLocalPack.Core.Library;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Library;

/// <summary>Status filter of the library (one chip each, single choice).</summary>
public enum LibraryFilter
{
    All,
    Installed,
    PackAvailable,
    NoPack,
    NeedsUpdate,

    /// <summary>Generating or paused.</summary>
    Generating,
}

public static class LibraryFilters
{
    /// <summary>Chip order.</summary>
    public static IReadOnlyList<LibraryFilter> All { get; } = Enum.GetValues<LibraryFilter>();

    /// <summary>Whether a game with <paramref name="status"/> passes <paramref name="filter"/>.</summary>
    public static bool Matches(LibraryFilter filter, LocalizationStatusKind status) => filter switch
    {
        LibraryFilter.All => true,
        LibraryFilter.Installed => status == LocalizationStatusKind.Installed,
        LibraryFilter.PackAvailable => status == LocalizationStatusKind.PackAvailable,
        LibraryFilter.NoPack => status == LocalizationStatusKind.NoPack,
        LibraryFilter.NeedsUpdate => status == LocalizationStatusKind.NeedsUpdate,
        LibraryFilter.Generating => status is LocalizationStatusKind.Generating or LocalizationStatusKind.Paused,
        _ => false,
    };
}

/// <summary>A filter chip: label, live count (games of the status, search not applied) and whether it is chosen.</summary>
public sealed partial class LibraryFilterChip(LibraryFilter filter, Action<LibraryFilter> select) : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _countText = "0";

    [ObservableProperty]
    private bool _isSelected;

    public LibraryFilter Filter { get; } = filter;

    /// <summary>Screen reader name: "Pack available, 4".</summary>
    public string AccessibleName => $"{Label}, {CountText}";

    [RelayCommand]
    private void Select() => select(Filter);

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    partial void OnCountTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
}
