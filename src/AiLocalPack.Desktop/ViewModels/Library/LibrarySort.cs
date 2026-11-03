namespace AiLocalPack.Desktop.ViewModels.Library;

/// <summary>Order of the library grid. <c>Important first</c> (by pack status) comes with registry statuses (task 21).</summary>
public enum LibrarySort
{
    /// <summary>Alphabetical in the UI language, ignoring case.</summary>
    ByName,

    /// <summary>Last played first; never played games after them, by name.</summary>
    RecentlyPlayed,
}

/// <summary>An entry of the sort dropdown.</summary>
public sealed record LibrarySortOption(LibrarySort Value, string Display);
