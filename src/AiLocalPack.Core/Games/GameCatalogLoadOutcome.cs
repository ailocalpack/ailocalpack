namespace AiLocalPack.Core.Games;

/// <summary>What <see cref="GameCatalog.Load"/> found on disk.</summary>
public enum GameCatalogLoadOutcome
{
    /// <summary><see cref="GameCatalog.Load"/> hasn't run yet.</summary>
    NotLoaded,

    /// <summary>The file was read (broken entries, if any, skipped).</summary>
    Loaded,

    /// <summary>No file yet (first launch): the catalog starts empty.</summary>
    Missing,

    /// <summary>The file wasn't valid JSON: it was moved aside (<see cref="GameCatalog.CorruptedBackupPath"/>) and the catalog starts empty.</summary>
    Corrupted,

    /// <summary>The file couldn't be read (locked, no access): the catalog starts empty and is not saved this session.</summary>
    Unreadable,
}
