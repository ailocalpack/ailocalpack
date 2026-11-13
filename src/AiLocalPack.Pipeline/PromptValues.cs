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
    public static Dictionary<string, string> ExtractUnknown(string gameName, string language, EngineKind engine, string gamePath, int files, string? toolkitError = null) => new(StringComparer.Ordinal)
    {
        ["gameName"] = gameName,
        ["engine"] = engine.Engine == GameEngine.Unknown ? "engine unknown"
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
}
