namespace AiLocalPack.Desktop.ViewModels.Library;

/// <summary>Order of the library grid.</summary>
public enum LibrarySort
{
    /// <summary>
    /// By status (needs update, paused, generating, pack available, installed, no pack, blocked), then last played;
    /// the default.
    /// </summary>
    ImportantFirst,

    /// <summary>Last played first; never played games after them, by name.</summary>
    RecentlyPlayed,

    /// <summary>Alphabetical in the UI language, ignoring case.</summary>
    ByName,
}

/// <summary>An entry of the sort dropdown.</summary>
public sealed record LibrarySortOption(LibrarySort Value, string Display);
