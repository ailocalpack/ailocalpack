using System.Text;
using System.Text.RegularExpressions;

namespace AiLocalPack.Pipeline;

/// <summary>
/// A prompt kept as a versioned resource file (<c>Prompts/&lt;name&gt;.v&lt;version&gt;.md</c>): plain text with
/// <c>{{name}}</c> fields and no other logic. A change that alters what the agent does gets a new version (a new file),
/// so a job log names the exact prompt it ran with; the rendered prompts are snapshot-tested.
/// </summary>
public sealed partial class PromptTemplate
{
    private PromptTemplate(string name, int version, string text)
    {
        Name = name;
        Version = version;
        Text = text;
        Fields = [.. FieldPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Appended to the agent's own system prompt in every stage.</summary>
    public static PromptTemplate System { get; } = Load("system", 1);

    /// <summary>The Glossary stage's task.</summary>
    public static PromptTemplate Glossary { get; } = Load("glossary", 1);

    /// <summary>An update job's Glossary stage (task 56): a glossary that follows the base pack's translations.</summary>
    public static PromptTemplate GlossaryUpdate { get; } = Load("glossary-update", 1);

    /// <summary>The Translate stage's task for one chunk.</summary>
    public static PromptTemplate Translate { get; } = Load("translate", 1);

    /// <summary>The Translate stage's task for the strings an earlier attempt at the chunk left without a valid translation.</summary>
    public static PromptTemplate TranslateRetry { get; } = Load("translate-retry", 1);

    /// <summary>The Study stage's task (Thorough jobs): read the whole game, optionally look it up on the web, reply with the game brief.</summary>
    public static PromptTemplate Study { get; } = Load("study", 1);

    /// <summary>Appended to the system prompt of a Thorough job's Glossary and Translate runs: the game brief from Study.</summary>
    public static PromptTemplate SystemBrief { get; } = Load("system-brief", 1);

    /// <summary>The system prompt of a run that writes and runs its own scripts in its workspace (task 57).</summary>
    public static PromptTemplate SystemScripts { get; } = Load("system-scripts", 1);

    /// <summary>Extract stage for a game the toolkit can't read: the agent writes and checks its extractor and rebuilder (task 57).</summary>
    public static PromptTemplate ExtractUnknown { get; } = Load("extract-unknown", 1);

    /// <summary>Build stage for such a game: the agent runs its rebuilder on the final translations (task 57).</summary>
    public static PromptTemplate RebuildUnknown { get; } = Load("rebuild-unknown", 1);

    public string Name { get; }

    public int Version { get; }

    /// <summary>The template as stored (line ends <c>\n</c>).</summary>
    public string Text { get; }

    /// <summary>The fields it uses, in order of first use.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary><c>glossary v1</c>, as logged.</summary>
    public string Label => $"{Name} v{Version}";

    /// <summary>The text with every field replaced; the trailing line end removed.</summary>
    /// <exception cref="ArgumentException">A field has no value, or a value names no field.</exception>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Fields.FirstOrDefault(f => !values.ContainsKey(f)) is { } missing)
        {
            throw new ArgumentException($"The {Label} prompt needs a value for {{{{{missing}}}}}.", nameof(values));
        }

        if (values.Keys.FirstOrDefault(k => !Fields.Contains(k, StringComparer.Ordinal)) is { } extra)
        {
            throw new ArgumentException($"The {Label} prompt has no {{{{{extra}}}}} field.", nameof(values));
        }

        return FieldPattern().Replace(Text, m => values[m.Groups[1].Value]).TrimEnd('\n');
    }

    internal static PromptTemplate Load(string name, int version)
    {
        var resource = $"AiLocalPack.Pipeline.Prompts.{name}.v{version}.md";
        using var stream = typeof(PromptTemplate).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The prompt {resource} isn't embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new PromptTemplate(name, version, reader.ReadToEnd().ReplaceLineEndings("\n"));
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex FieldPattern();
}
