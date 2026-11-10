using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Pipeline;

/// <summary>The stored translations as the Build stage takes them (<see cref="LocalPackBuilder.Collect"/>).</summary>
/// <param name="Translations">Key → translation of every string with an accepted translation (no validator error).</param>
/// <param name="Warnings">Validator warnings of those (the <c>untranslated</c> rule left out: <see cref="KeptAsIs"/> counts it).</param>
/// <param name="Untranslated">Strings without an accepted translation: they stay in the game's language.</param>
/// <param name="KeptAsIs">Accepted translations equal to the original (names, "HP").</param>
/// <param name="Refused">Stored translations the validator refuses now (left out, counted in <see cref="Untranslated"/>).</param>
public sealed record BuildTranslations(
    IReadOnlyDictionary<string, string> Translations,
    IReadOnlyList<LocalPackWarning> Warnings,
    IReadOnlyList<LocalPackString> Untranslated,
    int KeptAsIs,
    IReadOnlyList<(string Key, StringIssue Issue)> Refused)
{
    /// <summary>Translated = strings with an accepted translation (kept-as-is ones included), of every string the game has.</summary>
    public PackCoverage Coverage => new(Translations.Count, Translations.Count + Untranslated.Count);
}

/// <summary>What the manifest of a generated pack is made from.</summary>
/// <param name="Id">The pack ID.</param>
/// <param name="AgentId">The job's agent ID (<c>claude</c>, <c>codex</c>…).</param>
/// <param name="AgentName">Its display name, for the title.</param>
/// <param name="Agent">Version and model as last seen, if known.</param>
/// <param name="Tool">Recipe tool ID and its version (<c>major.minor.patch</c>).</param>
/// <param name="Touched">Files the translations rewrite → their SHA-256 in the working copy (the game as extracted).</param>
/// <param name="Fonts">Whitelisted fonts swapped in.</param>
/// <param name="Model">The job's own model option, when the agent didn't report one.</param>
/// <param name="Base">An update job's base pack (task 56): the pack is its next revision.</param>
/// <param name="Update">What the update changed, for the new revision's notes.</param>
/// <param name="Patch">A binary patch pack (task 57, a game the toolkit can't read): the manifest has this <c>patch</c>
/// instead of a recipe; <paramref name="Touched"/> = the patched files' originals.</param>
public sealed record LocalPackInput(
    string Id,
    Game Game,
    string Language,
    string AgentId,
    string AgentName,
    AgentRunInfo? Agent,
    string? Model,
    string Tool,
    Version ToolVersion,
    IReadOnlyDictionary<string, string> Touched,
    IReadOnlyList<string> Fonts,
    PackCoverage Coverage,
    DateTimeOffset Now,
    PackManifest? Base = null,
    LocalPackUpdate? Update = null,
    PackPatch? Patch = null);

/// <summary>
/// Turns a finished translation into a local pack (task 51). Pure. The string table holds the accepted translations
/// only; coverage = their count of every extracted string; the manifest is filled from the job: game, the build
/// (Steam build ID; none for a manually added game) with the touched files' original hashes, recipe = the tool that
/// extracted the strings in this app's version, agent (manifest IDs <c>claude-code</c> / <c>codex</c>), fonts, no
/// dependencies (no toolkit tool needs one), status <c>alpha</c>, revision 1 by <see cref="LocalPackStore.PlaceholderAuthor"/>
/// (the player's GitHub login replaces it when publishing, task 60), license CC-BY-SA-4.0. An update (task 56) is the
/// base pack's next revision instead: its ID, title, authors (+ the placeholder), revisions (+ one with notes naming what
/// changed) and creation date, with the new build only. The string table keeps each translation's
/// <see cref="OriginalHash"/>, so a later update can tell what changed.
/// </summary>
public static class LocalPackBuilder
{
    /// <summary>Title of a generated pack.</summary>
    public static string Title(string agentName) => $"{agentName} translation";

    /// <summary>The manifest's agent ID of a job's agent (the registry's IDs: <c>claude-code</c>, <c>codex</c>).</summary>
    public static string ManifestAgentId(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        return agentId == "claude" ? "claude-code" : agentId;
    }

    /// <summary>The pack ID a new local pack starts from (<see cref="LocalPackStore.FreeId"/> makes it unique).</summary>
    public static string PreferredId(string agentId) => ManifestAgentId(agentId);

    /// <summary>
    /// Every extracted string against the store, in the source's order: an empty or missing text is untranslated, one the
    /// validator refuses (stored before a rule changed, or edited by hand) too; warnings are kept.
    /// </summary>
    public static BuildTranslations Collect(SourceStrings source, StringTable? stored, TextMarkup markup)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(markup);
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<LocalPackWarning>();
        var untranslated = new List<LocalPackString>();
        var refused = new List<(string, StringIssue)>();
        var keptAsIs = 0;
        foreach (var s in source.Strings)
        {
            if (stored?.Strings.TryGetValue(s.Key, out var text) != true || string.IsNullOrEmpty(text))
            {
                untranslated.Add(new LocalPackString(s.Key, s.Original));
                continue;
            }

            var issues = StringValidator.Validate(s, text, markup);
            if (issues.FirstOrDefault(i => i.Severity == StringIssueSeverity.Error) is { } error)
            {
                refused.Add((s.Key, error));
                untranslated.Add(new LocalPackString(s.Key, s.Original));
                continue;
            }

            translations[s.Key] = text;
            if (string.Equals(text, s.Original, StringComparison.Ordinal))
            {
                keptAsIs++;
            }

            warnings.AddRange(issues
                .Where(i => i.Rule != StringValidator.Untranslated)
                .Select(i => new LocalPackWarning(s.Key, i.Rule, i.Message, s.Original, text)));
        }

        return new BuildTranslations(translations, warnings, untranslated, keptAsIs, refused);
    }

    /// <summary>The pack's string table: the accepted translations with the hashes of their originals.</summary>
    public static StringTable Table(SourceStrings source, BuildTranslations collected, string language)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(collected);
        var originals = source.Strings
            .Where(s => collected.Translations.ContainsKey(s.Key))
            .ToDictionary(s => s.Key, s => OriginalHash.Of(s.Original), StringComparer.Ordinal);
        return new StringTable(language, collected.Translations) { Originals = originals };
    }

    /// <summary>"Update for build 123: 12 new strings, 3 changed, 2 removed."</summary>
    public static string RevisionNotes(LocalPackUpdate update, string? buildId) =>
        $"Update for {(buildId is { Length: > 0 } ? $"build {buildId}" : "the game's new version")}: {update.Added} new strings, {update.Changed} changed, {update.Removed} removed.";

    /// <summary>The manifest of the pack (its <c>strings</c> entry is set when the store writes the table).</summary>
    /// <exception cref="ArgumentException">A Steam game without a build ID.</exception>
    public static PackManifest Manifest(LocalPackInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string? buildId = null;
        if (input.Game.Id.SteamAppId is not null)
        {
            buildId = input.Game.Version?.BuildId is { Length: > 0 } id ? id : throw new ArgumentException("A Steam game's pack needs the game's build ID.", nameof(input));
        }

        var utc = input.Now.ToUniversalTime();
        var now = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
        var model = input.Agent?.Model ?? input.Model;
        var placeholder = LocalPackStore.PlaceholderAuthor;
        IReadOnlyList<PackAuthor> authors = [new PackAuthor(placeholder)];
        IReadOnlyList<PackRevision> revisions = [new PackRevision(1, now, placeholder)];
        var title = Title(input.AgentName);
        var created = now;
        if (input.Base is { } basePack)
        {
            // Dates never go back, even when this clock is behind the base's author's.
            now = new[] { now, basePack.Updated.ToUniversalTime(), basePack.Revisions[^1].Date.ToUniversalTime() }.Max();
            authors = basePack.Authors.Any(a => a.GitHub == placeholder) ? basePack.Authors : [.. basePack.Authors, new PackAuthor(placeholder)];
            var notes = input.Update is { } update ? RevisionNotes(update, buildId) : null;
            revisions = [.. basePack.Revisions, new PackRevision(basePack.Revision + 1, now, placeholder, notes)];
            title = basePack.Title;
            created = basePack.Created.ToUniversalTime();
        }

        return new PackManifest(
            input.Id,
            input.Game.Id,
            input.Language,
            title,
            authors,
            revisions,
            PackStatus.Alpha,
            new PackAgent(ManifestAgentId(input.AgentId), input.Agent?.Version, model),
            [new PackBuild(buildId, new SortedDictionary<string, string>(input.Touched.ToDictionary(), StringComparer.Ordinal))],
            input.Patch is null ? new PackRecipe(input.Tool, input.ToolVersion.ToString(3)) : null,
            null,
            input.Patch,
            [],
            [.. input.Fonts],
            input.Coverage,
            created,
            now);
    }
}
