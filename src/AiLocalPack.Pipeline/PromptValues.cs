using System.Globalization;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Pipeline;

/// <summary>The field values of the prompts (<see cref="PromptTemplate"/>).</summary>
public static class PromptValues
{
    /// <summary>The language's English name (<c>Russian</c>, <c>Spanish (Latin America)</c>); the tag itself when the OS doesn't know it.</summary>
    public static string LanguageName(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag, predefinedOnly: true);
            return string.IsNullOrEmpty(culture.EnglishName) || culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? tag : culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    public static Dictionary<string, string> System(string gameName, string language) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["languageName"] = LanguageName(language),
        ["languageTag"] = language,
    };

    public static Dictionary<string, string> Glossary(string gameName, string language, EngineKind engine, int strings, int files) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["engine"] = engine.DisplayName,
        ["languageName"] = LanguageName(language),
        ["stringCount"] = strings.ToString("N0", CultureInfo.InvariantCulture),
        ["fileCount"] = files.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>The Study prompt's step 2: what the agent may use besides the strings.</summary>
    public static string StudyWebStep(bool webLookup) => webLookup
        ? "2. Look the game up on the web with your web search and fetch tools: its store page, wikis and fan sites, and its official translations into other languages, to learn what the strings don't say (who the characters are, names an official translation already uses). Search for the game and its characters, never for the text of the strings, and keep it to about 20 searches."
        : "2. You have no web access: work from the strings alone.";

    public static Dictionary<string, string> Study(string gameName, string language, EngineKind engine, int strings, int files, bool webLookup)
    {
        var values = Glossary(gameName, language, engine, strings, files);
        values["webStep"] = StudyWebStep(webLookup);
        return values;
    }

    public static Dictionary<string, string> SystemBrief(string brief) => new(StringComparer.Ordinal) { ["brief"] = brief };

    /// <summary>At most this many files the references changed are named to an Extract run.</summary>
    public const int ReferenceFilesShown = 30;

    /// <param name="files">The extract run's hint: the game files the references changed; <c>null</c> for other runs.</param>
    public static Dictionary<string, string> SystemReferences(string gameName, string language, Core.Generation.ReferenceSet references, IReadOnlyList<string>? files)
    {
        ArgumentNullException.ThrowIfNull(references);
        var packs = string.Join("; ", references.Packs.Select(p =>
            $"{LanguageName(p.Manifest.Language)} (\"{p.Manifest.Title}\" rev {p.Manifest.Revision}"
            + (p.Manifest.Status == Core.Packs.PackStatus.Verified ? ", verified" : "")
            + (p.SameBuild ? ", made for this version of the game" : ", made for another version of the game")
            + (p.Manifest.Patch is not null ? ", a binary patch: no strings to compare" : "")
            + ")"));
        var filesNote = files is { Count: > 0 }
            ? "\n- The game files these translations changed are where the text most likely is; look there first: "
                + string.Join(", ", files.Take(ReferenceFilesShown).Select(f => $"`{f}`"))
                + (files.Count > ReferenceFilesShown ? $" and {files.Count - ReferenceFilesShown} more." : ".")
            : string.Empty;
        return new(StringComparer.Ordinal)
        {
            ["gameName"] = gameName,
            ["languageName"] = LanguageName(language),
            ["packs"] = packs,
            ["filesNote"] = filesNote,
        };
    }

    /// <param name="baseTitle">The base pack's title.</param>
    /// <param name="kept">Strings that already have the base's translation.</param>
    public static Dictionary<string, string> GlossaryUpdate(string gameName, string language, EngineKind engine, int strings, int files, string baseTitle, int kept)
    {
        var values = Glossary(gameName, language, engine, strings, files);
        values["baseTitle"] = baseTitle;
        values["keptCount"] = kept.ToString("N0", CultureInfo.InvariantCulture);
        return values;
    }

    /// <param name="gamePath">The working copy, which the agent reads but never writes (task 57).</param>
    public static Dictionary<string, string> SystemScripts(string gameName, string language, string gamePath)
    {
        var values = System(gameName, language);
        values["gamePath"] = gamePath;
        return values;
    }

    /// <param name="toolkitError">The toolkit's own reader failed on the game with this message (the engine is known).</param>
    /// <param name="chosen">The player chose the agent's own code over the toolkit (<see cref="Core.Generation.GenerationOptions.AgentReader"/>).</param>
    public static Dictionary<string, string> ExtractUnknown(string gameName, string language, EngineKind engine, string gamePath, int files, string? toolkitError = null, bool chosen = false) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["engine"] = chosen ? (engine.Engine == GameEngine.Unknown ? "engine unknown" : engine.DisplayName) + "; the player asked for your own reader instead of the toolkit's"
            : engine.Engine == GameEngine.Unknown ? "engine unknown"
            : toolkitError is null ? engine.DisplayName
            : $"{engine.DisplayName}; the toolkit's reader stopped with: {toolkitError.TrimEnd('.')}",
        ["gamePath"] = gamePath,
        ["fileCount"] = files.ToString("N0", CultureInfo.InvariantCulture),
        ["languageName"] = LanguageName(language),
        ["languageTag"] = language,
    };

    public static Dictionary<string, string> RebuildUnknown(string gameName, string language, string gamePath, int translations) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["gamePath"] = gamePath,
        ["translationCount"] = translations.ToString("N0", CultureInfo.InvariantCulture),
        ["languageName"] = LanguageName(language),
    };

    /// <summary>The fix system prompt's paragraph on where the agent works: the toolkit's tools, or its own scripts' workspace (unknown engines).</summary>
    public static string FixWorkspace(string? gamePath) => gamePath is null
        ? "Your working folder is a copy of the game's files. Work only through the tools of the `ailocalpack` server: `search` and `get_strings` find strings with their translations, `put_translations` stores fixed translations, `get_glossary` and `put_glossary` read and change the glossary. Never try to change the game's files: the app rebuilds them from the translations."
        : $"The toolkit can't read this game's files, so earlier you wrote the extract and rebuild scripts in `scripts/` of your workspace (your working folder; `scripts/README.md` says how they run). A copy of the game is at `{gamePath}`: read it, never change it. Your commands run in a sandbox that can write only inside the workspace and has no network. Strings and their translations are reached with the `ailocalpack` tools (`search`, `get_strings`, `put_translations`, `get_glossary`, `put_glossary`).";

    public static Dictionary<string, string> SystemFix(string gameName, string language, string? gamePath) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["languageName"] = LanguageName(language),
        ["languageTag"] = language,
        ["workspace"] = FixWorkspace(gamePath),
    };

    /// <param name="attachments">What the player sent, rendered (<see cref="FixPrompt.Attachments"/>).</param>
    /// <param name="history">The conversation so far, rendered (<see cref="FixPrompt.History"/>).</param>
    /// <param name="scripts">The game is read by the agent's own scripts (unknown engines).</param>
    public static Dictionary<string, string> Fix(string gameName, EngineKind engine, int strings, int draft, string request, string attachments, string history, bool scripts) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["engine"] = engine.Engine == GameEngine.Unknown ? "engine unknown" : engine.DisplayName,
        ["stringCount"] = strings.ToString("N0", CultureInfo.InvariantCulture),
        ["draft"] = draft.ToString(CultureInfo.InvariantCulture),
        ["request"] = request.Trim(),
        ["attachments"] = attachments,
        ["history"] = history,
        ["scriptsStep"] = scripts
            ? ", or your rebuild script in `scripts/` when the problem is how the files are written (check the round trip as before; the app runs your rebuild again for the next draft). Whatever the scripts change beyond the strings (a font with the missing letters, a file kept under a size the game reads) must be on by default and described in `scripts/README.md`: the app's rebuild run follows the README and keeps it"
            : string.Empty,
    };
}
