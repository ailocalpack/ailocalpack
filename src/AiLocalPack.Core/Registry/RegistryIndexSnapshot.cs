using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Registry;

public enum RegistryIndexState
{
    /// <summary>Just read from the registry (or the server confirmed the saved copy is current).</summary>
    Fresh,

    /// <summary>The registry couldn't be read; this is the saved copy from <see cref="RegistryIndexSnapshot.FetchedAt"/>.</summary>
    Stale,

    /// <summary>The registry couldn't be read and there is no saved copy: no index at all.</summary>
    Unavailable,
}

/// <summary>Why the registry couldn't be read this time.</summary>
public enum RegistryIndexProblem
{
    None,

    /// <summary>No connection (DNS, refused, reset…).</summary>
    Offline,

    /// <summary>No answer within the timeout.</summary>
    Timeout,

    /// <summary>The server answered with an error status.</summary>
    HttpError,

    /// <summary>The downloaded (or local) file is not a valid index.</summary>
    Malformed,

    /// <summary>The index is in a newer format: the app needs an update.</summary>
    NewerFormat,

    /// <summary>A local registry folder has no <c>index.json</c>, or it can't be read.</summary>
    LocalFileMissing,

    /// <summary>The override (<c>AILOCALPACK_INDEX_URL</c> or the setting) is not a usable address.</summary>
    InvalidLocation,
}

/// <summary>What the app knows about the registry after a fetch: the index to show, how current it is, and why not when it isn't.</summary>
/// <param name="Location">Where it was read from (<c>null</c> when the override itself is invalid).</param>
/// <param name="FetchedAt">When the registry last confirmed <see cref="Index"/> ("stale since" when <see cref="State"/> is stale).</param>
/// <param name="Detail">Plain English for logs and the CLI ("HTTP 503", "timed out after 15 s").</param>
/// <param name="SkippedEntries">Damaged entries left out of <see cref="Index"/>.</param>
public sealed record RegistryIndexSnapshot(
    RegistryLocation? Location,
    RegistryIndexState State,
    RegistryIndex? Index,
    DateTimeOffset? FetchedAt,
    RegistryIndexProblem Problem = RegistryIndexProblem.None,
    string? Detail = null,
    IReadOnlyList<PackFormatError>? SkippedEntries = null)
{
    public bool IsFresh => State == RegistryIndexState.Fresh;

    public bool IsStale => State == RegistryIndexState.Stale;

    public bool HasIndex => Index is not null;

    public IReadOnlyList<PackFormatError> Skipped => SkippedEntries ?? [];

    public static RegistryIndexSnapshot InvalidLocation(string error) =>
        new(null, RegistryIndexState.Unavailable, null, null, RegistryIndexProblem.InvalidLocation, error);
}
