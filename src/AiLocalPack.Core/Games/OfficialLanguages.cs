namespace AiLocalPack.Core.Games;

/// <summary>A language the game itself ships, as its store lists it.</summary>
/// <param name="Tag">A <see cref="Languages.GameLanguages.Tags"/> tag.</param>
/// <param name="Interface">Menus and text are in this language (Steam's "Interface").</param>
/// <param name="Subtitles">Subtitles are (Steam's "Subtitles").</param>
/// <param name="FullAudio">The game is voiced in it (Steam's "Full Audio").</param>
public sealed record OfficialLanguage(string Tag, bool Interface, bool Subtitles, bool FullAudio)
{
    /// <summary>The game's text reads in this language: the player needs no text translation (the MVP translates text only).</summary>
    public bool HasText => Interface || Subtitles;
}

/// <summary>Where <see cref="OfficialLanguages"/> came from.</summary>
public enum OfficialLanguagesSource
{
    /// <summary>The Steam client's own app info cache (<c>appcache/appinfo.vdf</c>): no network, interface / subtitles / audio apart.</summary>
    SteamClient,

    /// <summary>The Steam store's public app details (when the client's cache doesn't have the game): no interface / subtitles split.</summary>
    SteamStore,
}

/// <summary>The languages a game ships with (task 80), from its store.</summary>
/// <param name="Languages">Every known language, in the store's order; empty when the store lists none (or doesn't know the game).</param>
/// <param name="Checked">When they were read.</param>
/// <param name="SteamLanguage">
/// The language Steam starts the game in (the app manifest's <c>UserConfig/language</c>), when the client says; <c>null</c>
/// otherwise.
/// </param>
public sealed record OfficialLanguages(
    IReadOnlyList<OfficialLanguage> Languages,
    OfficialLanguagesSource Source,
    DateTimeOffset Checked,
    string? SteamLanguage = null)
{
    /// <summary>The entry for <paramref name="tag"/> (exact tag, case-insensitive); <c>null</c> when the game doesn't list it.</summary>
    public OfficialLanguage? Find(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return Languages.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The entry for <paramref name="tag"/> when the game's text reads in it (<see cref="OfficialLanguage.HasText"/>).</summary>
    public OfficialLanguage? TextIn(string tag) => Find(tag) is { HasText: true } language ? language : null;

    /// <summary>Same languages, source and Steam language (the check time aside).</summary>
    public bool SameAs(OfficialLanguages? other) =>
        other is not null
        && Source == other.Source
        && string.Equals(SteamLanguage, other.SteamLanguage, StringComparison.Ordinal)
        && Languages.SequenceEqual(other.Languages);
}
