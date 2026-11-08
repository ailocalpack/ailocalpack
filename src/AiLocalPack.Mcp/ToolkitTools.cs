using System.ComponentModel;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Validation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiLocalPack.Mcp;

/// <summary>
/// The tools the agent calls. Results are small and paged (<c>offset</c> + <c>limit</c> in, <c>total</c> +
/// <c>nextOffset</c> out, <c>null</c> = last page), string pages also stop at <see cref="MaxPageCharacters"/>, so a
/// result fits the agent's context. User mistakes (a path outside the copy, an unknown key) come back as tool errors
/// whose text the agent reads.
/// </summary>
[McpServerToolType]
public sealed class ToolkitTools(ToolkitSession session)
{
    /// <summary>Text a page of strings, glossary entries or a snippet holds at most (characters), whatever the limit.</summary>
    public const int MaxPageCharacters = 20_000;

    /// <summary>Entries a recursive listing walks at most.</summary>
    public const int MaxListedEntries = 200_000;

    private const int MaxStrings = 200;
    private const int MaxFiles = 500;
    private const int MaxIssues = 200;
    private const int MaxSnippetLines = 400;
    private const int MaxLineLength = 1_000;
    private const int MaxHexBytes = 4_096;
    private const int ListedUnknownKeys = 20;
    private const long MaxCountedFileSize = 32L * 1024 * 1024;

    [McpServerTool(Name = "detect_engine", ReadOnly = true, Idempotent = true)]
    [Description("Detects the game's engine and whether the toolkit can extract and rebuild its strings.")]
    public DetectResult DetectEngine()
    {
        lock (session.Gate)
        {
            var detection = session.Detect(refresh: true);
            var kind = detection.Kind;
            var problem = session.ToolProblem(kind);
            var tool = problem is null ? session.Tools.For(kind) : null;
            return new DetectResult(
                kind.ToString(),
                kind.DisplayName,
                detection.Match?.Confidence.ToString().ToLowerInvariant(),
                detection.Match?.Marker,
                detection.Match?.MainExecutable,
                detection.IsAmbiguous,
                [.. detection.Candidates.Select(c => new EngineCandidate(c.Kind.ToString(), c.Kind.DisplayName, c.Marker))],
                tool?.Id,
                tool is null ? null : Version(tool.Version),
                problem);
        }
    }

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true)]
    [Description("Lists files and folders of the game folder (paths relative to it, '/'-separated). Links are listed, never followed.")]
    public FileListResult ListFiles(
        [Description("Folder relative to the game folder; '.' or empty = the game folder.")] string? path = null,
        [Description("File name pattern with * and ? (e.g. '*.json'); folders are listed only without a pattern.")] string? pattern = null,
        [Description("Include every subfolder.")] bool recursive = false,
        [Description("Entries to skip.")] int offset = 0,
        [Description("Entries to return (at most 500).")] int limit = 100)
    {
        lock (session.Gate)
        {
            var folder = session.Copy.Resolve(path);
            if (!Directory.Exists(folder))
            {
                throw new McpException(File.Exists(folder) ? $"'{path}' is a file, not a folder." : $"Folder '{path}' not found.");
            }

            var (entries, truncated) = Walk(folder, string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim(), recursive);
            var (start, count) = Window(offset, limit, MaxFiles, entries.Count);
            return new FileListResult(
                session.Copy.Relative(folder),
                entries.Count,
                [.. entries.Skip(start).Take(count)],
                Next(start + count, entries.Count),
                truncated);
        }
    }

    [McpServerTool(Name = "read_file_snippet", ReadOnly = true, Idempotent = true)]
    [Description("Reads part of a file: lines of a text file, or a hex dump of a binary file's bytes.")]
    public SnippetResult ReadFileSnippet(
        [Description("File relative to the game folder.")] string path,
        [Description("Text: first line to return (1-based).")] int startLine = 1,
        [Description("Text: lines to return (at most 400).")] int lineCount = 100,
        [Description("Binary: first byte to dump.")] long byteOffset = 0,
        [Description("Binary: bytes to dump (at most 4096).")] int byteCount = 512)
    {
        lock (session.Gate)
        {
            var file = session.Copy.Resolve(path);
            if (!File.Exists(file))
            {
                throw new McpException(Directory.Exists(file) ? $"'{path}' is a folder; use list_files." : $"File '{path}' not found.");
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return LooksBinary(stream)
                    ? Hex(session.Copy.Relative(file), stream, byteOffset, byteCount)
                    : Lines(session.Copy.Relative(file), stream, startLine, lineCount);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"Can't read '{path}': {e.Message}", e);
            }
        }
    }

    [McpServerTool(Name = "extract_strings", ReadOnly = true, Idempotent = true)]
    [Description("The game's translatable strings as the toolkit extracts them (originals with context, length limit and placeholders to keep), paged.")]
    public StringPage ExtractStrings(
        [Description("Strings to skip.")] int offset = 0,
        [Description("Strings to return (at most 200; a page also stops at about 20 000 characters).")] int limit = 50,
        [Description("Only keys starting with this (keys begin with the file they come from).")] string? keyPrefix = null,
        [Description("Only strings whose original or key contains this text (ignoring case).")] string? search = null,
        [Description("Extract again instead of using this session's copy (after the game files changed).")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        lock (session.Gate)
        {
            var extracted = session.Extract(refresh, cancellationToken);
            var stored = session.ReadStrings().Strings;
            var matching = extracted.Result.Strings
                .Where(s => keyPrefix is null || s.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .Where(s => string.IsNullOrEmpty(search)
                    || s.Original.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || s.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var (start, items) = Page(matching, offset, limit, s => Item(s, stored.GetValueOrDefault(s.Key)));
            return new StringPage(
                extracted.Tool.Id,
                Version(extracted.Tool.Version),
                extracted.Result.Strings.Count,
                extracted.Result.Files.Count,
                stored.Count,
                matching.Count,
                items,
                Next(start + items.Count, matching.Count),
                []);
        }
    }

    [McpServerTool(Name = "get_strings", ReadOnly = true, Idempotent = true)]
    [Description("Strings with their stored translations: the keys asked for, or a page of all / translated / untranslated ones.")]
    public StringPage GetStrings(
        [Description("Keys to return (instead of a page).")] string[]? keys = null,
        [Description("Without keys: 'all', 'translated' or 'untranslated'.")] string status = "untranslated",
        [Description("Strings to skip.")] int offset = 0,
        [Description("Strings to return (at most 200; a page also stops at about 20 000 characters).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        lock (session.Gate)
        {
            var extracted = session.Extract(cancellationToken: cancellationToken);
            var stored = session.ReadStrings().Strings;
            List<ExtractedString> selected;
            List<string> unknown = [];
            if (keys is { Length: > 0 })
            {
                selected = [];
                foreach (var key in keys.Distinct(StringComparer.Ordinal))
                {
                    if (extracted.ByKey.TryGetValue(key, out var item))
                    {
                        selected.Add(item);
                    }
                    else
                    {
                        unknown.Add(key);
                    }
                }
            }
            else
            {
                selected = status switch
                {
                    "all" => [.. extracted.Result.Strings],
                    "translated" => [.. extracted.Result.Strings.Where(s => stored.ContainsKey(s.Key))],
                    "untranslated" => [.. extracted.Result.Strings.Where(s => !stored.ContainsKey(s.Key))],
                    _ => throw new McpException($"Unknown status '{status}': use all, translated or untranslated."),
                };
            }

            var (start, items) = Page(selected, offset, limit, s => Item(s, stored.GetValueOrDefault(s.Key)));
            return new StringPage(
                extracted.Tool.Id,
                Version(extracted.Tool.Version),
                extracted.Result.Strings.Count,
                extracted.Result.Files.Count,
                stored.Count,
                selected.Count,
                items,
                Next(start + items.Count, selected.Count),
                unknown);
        }
    }

    [McpServerTool(Name = "put_translations", Destructive = false, Idempotent = true)]
    [Description("Stores translations by key in the job's string store. Each is validated first: one with an error (missing placeholder, too long, empty) is refused, warnings are stored and reported. An empty text removes a stored translation.")]
    public PutTranslationsResult PutTranslations(
        [Description("Key → translated text.")] Dictionary<string, string> translations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translations);
        lock (session.Gate)
        {
            var language = session.WriteLanguage();
            var extracted = session.Extract(cancellationToken: cancellationToken);
            var table = session.ReadStrings();
            var strings = new SortedDictionary<string, string>(table.Strings.ToDictionary(), StringComparer.Ordinal);
            var context = new SortedDictionary<string, string>(table.Context.ToDictionary(), StringComparer.Ordinal);
            var rejected = new List<KeyIssues>();
            var warnings = new List<KeyIssues>();
            List<string> unknown = [];
            var stored = 0;
            var removed = 0;
            foreach (var (key, text) in translations.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!extracted.ByKey.TryGetValue(key, out var source))
                {
                    unknown.Add(key);
                    continue;
                }

                if (string.IsNullOrEmpty(text))
                {
                    removed += strings.Remove(key) ? 1 : 0;
                    context.Remove(key);
                    continue;
                }

                var issues = StringValidator.Validate(source, text, extracted.Tool.Markup);
                if (issues.Any(i => i.Severity == StringIssueSeverity.Error))
                {
                    rejected.Add(new KeyIssues(key, [.. issues.Select(Issue)]));
                    continue;
                }

                if (issues.Count > 0)
                {
                    warnings.Add(new KeyIssues(key, [.. issues.Select(Issue)]));
                }

                strings[key] = text;
                if (source.Context is { } note)
                {
                    context[key] = note;
                }

                stored++;
            }

            if (stored > 0 || removed > 0)
            {
                session.WriteStrings(new StringTable(language, strings, context));
            }

            return new PutTranslationsResult(stored, removed, rejected, warnings, unknown, strings.Count, extracted.Result.Strings.Count);
        }
    }

    [McpServerTool(Name = "validate_strings", ReadOnly = true, Idempotent = true)]
    [Description("Checks translations against the originals (placeholders and tags kept, none added, printf fields in order, tags balanced, length limit, emptiness, whitespace, line breaks): the texts given, else the stored translations (all, or the keys given). Errors block a translation, warnings don't.")]
    public ValidateResult ValidateStrings(
        [Description("Key → text to check without storing it.")] Dictionary<string, string>? translations = null,
        [Description("Stored translations to check (default all).")] string[]? keys = null,
        [Description("Issues to skip.")] int offset = 0,
        [Description("Issues to return (at most 200).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        lock (session.Gate)
        {
            var extracted = session.Extract(cancellationToken: cancellationToken);
            IEnumerable<KeyValuePair<string, string>> checking = translations is { Count: > 0 }
                ? translations
                : keys is { Length: > 0 }
                    ? session.ReadStrings().Strings.Where(e => keys.Contains(e.Key, StringComparer.Ordinal))
                    : session.ReadStrings().Strings;
            var issues = new List<KeyIssue>();
            List<string> unknown = [];
            var checkedCount = 0;
            foreach (var (key, text) in checking.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!extracted.ByKey.TryGetValue(key, out var source))
                {
                    unknown.Add(key);
                    continue;
                }

                checkedCount++;
                issues.AddRange(StringValidator.Validate(source, text, extracted.Tool.Markup).Select(i => new KeyIssue(key, Severity(i.Severity), i.Rule, i.Message)));
            }

            var (start, count) = Window(offset, limit, MaxIssues, issues.Count);
            return new ValidateResult(
                checkedCount,
                issues.Count(i => i.Severity == "error"),
                issues.Count(i => i.Severity == "warning"),
                [.. issues.Skip(start).Take(count)],
                Next(start + count, issues.Count),
                unknown);
        }
    }

    [McpServerTool(Name = "get_glossary", ReadOnly = true, Idempotent = true)]
    [Description("The job's glossary (tone and terms with their translations), paged.")]
    public GlossaryPage GetGlossary(
        [Description("Only terms whose term, translation or note contains this text (ignoring case).")] string? search = null,
        [Description("Entries to skip.")] int offset = 0,
        [Description("Entries to return (at most 200).")] int limit = 100)
    {
        lock (session.Gate)
        {
            var glossary = session.ReadGlossary();
            var entries = (glossary?.Entries ?? [])
                .Where(e => string.IsNullOrEmpty(search)
                    || e.Term.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || e.Translation.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (e.Note?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
            var (start, items) = Page(entries, offset, limit, e => e);
            return new GlossaryPage(glossary?.Language, glossary?.Tone, glossary?.Entries.Count ?? 0, entries.Count, items, Next(start + items.Count, entries.Count));
        }
    }

    [McpServerTool(Name = "put_glossary", Destructive = false, Idempotent = true)]
    [Description("Adds or replaces glossary terms (matched ignoring case), removes terms, and sets the tone / form of address.")]
    public PutGlossaryResult PutGlossary(
        [Description("Terms to add or replace.")] GlossaryEntry[]? entries = null,
        [Description("Terms to remove.")] string[]? remove = null,
        [Description("Tone and form of address (e.g. 'informal, playful; the heroine says ты to everyone'); empty clears it.")] string? tone = null)
    {
        lock (session.Gate)
        {
            var language = session.WriteLanguage();
            var glossary = session.ReadGlossary() ?? Glossary.Empty(language);
            var list = glossary.Entries.ToList();
            var added = 0;
            var replaced = 0;
            foreach (var entry in entries ?? [])
            {
                var clean = Clean(entry);
                var at = list.FindIndex(e => Glossary.TermComparer.Equals(e.Term, clean.Term));
                if (at >= 0)
                {
                    list[at] = clean;
                    replaced++;
                }
                else
                {
                    list.Add(clean);
                    added++;
                }
            }

            var removedCount = list.RemoveAll(e => (remove ?? []).Contains(e.Term, Glossary.TermComparer));
            var newTone = tone is null ? glossary.Tone : string.IsNullOrWhiteSpace(tone) ? null : Text(tone.Trim(), "tone");
            session.WriteGlossary(new Glossary(language, newTone, list));
            return new PutGlossaryResult(added, replaced, removedCount, list.Count, newTone);
        }
    }

    [McpServerTool(Name = "rebuild", Destructive = false, Idempotent = true)]
    [Description("Rebuilds the game files from the stored translations into the job's build folder (the game folder stays as it is) and lists what changed.")]
    public RebuildToolResult Rebuild(CancellationToken cancellationToken = default)
    {
        lock (session.Gate)
        {
            var table = session.ReadStrings();
            if (table.Strings.Count == 0)
            {
                throw new McpException("Nothing to rebuild: no translations are stored yet (put_translations).");
            }

            var (tool, _) = session.Tool();
            var output = session.Files.BuildFolder;
            RebuildResult result;
            try
            {
                if (Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }

                result = tool.Rebuild(session.Copy.Root, table.Language, table.Strings, new DirectoryRebuildOutput(output), cancellationToken);
            }
            catch (RecipeException e)
            {
                throw new McpException(e.File is { } file ? $"{file}: {e.Message}" : e.Message, e);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"Can't write the build folder: {e.Message}", e);
            }

            return new RebuildToolResult(
                tool.Id,
                output,
                result.ChangedFiles,
                result.Applied,
                result.UnknownKeys.Count,
                [.. result.UnknownKeys.Take(ListedUnknownKeys)],
                result.GeneratedFiles,
                result.InGameLanguage);
        }
    }

    private static StringItem Item(ExtractedString s, string? translation) =>
        new(s.Key, s.Original, translation, s.Context, s.MaxLength, s.Placeholders);

    private static IssueText Issue(StringIssue issue) => new(Severity(issue.Severity), issue.Rule, issue.Message);

    private static string Severity(StringIssueSeverity severity) => severity == StringIssueSeverity.Error ? "error" : "warning";

    private static string Version(Version version) => version.ToString(3);

    private static GlossaryEntry Clean(GlossaryEntry entry)
    {
        var term = string.IsNullOrWhiteSpace(entry?.Term) ? throw new McpException("A glossary entry has no term.") : Text(entry.Term.Trim(), "term");
        var translation = entry.KeepAsIs ? term
            : string.IsNullOrWhiteSpace(entry.Translation) ? throw new McpException($"Term '{term}' has no translation (or set keepAsIs).")
            : Text(entry.Translation.Trim(), "translation");
        return new GlossaryEntry(term, translation, Optional(entry.Note, "note"), Optional(entry.PartOfSpeech, "partOfSpeech"), Optional(entry.Gender, "gender"), entry.KeepAsIs);
    }

    private static string? Optional(string? text, string field) => string.IsNullOrWhiteSpace(text) ? null : Text(text.Trim(), field);

    private static string Text(string text, string field) => text.Length <= Glossary.MaxTextLength
        ? text
        : throw new McpException($"The {field} is longer than {Glossary.MaxTextLength} characters.");

    private static (int Start, int Count) Window(int offset, int limit, int max, int total)
    {
        var start = Math.Clamp(offset, 0, total);
        return (start, Math.Min(Math.Clamp(limit, 1, max), total - start));
    }

    private static int? Next(int end, int total) => end < total ? end : null;

    /// <summary>A page of up to <paramref name="limit"/> items that also stops before <see cref="MaxPageCharacters"/> (always at least one item).</summary>
    private static (int Start, List<TOut> Items) Page<TIn, TOut>(IReadOnlyList<TIn> all, int offset, int limit, Func<TIn, TOut> map)
    {
        var (start, count) = Window(offset, limit, MaxStrings, all.Count);
        var items = new List<TOut>();
        var characters = 0;
        foreach (var item in all.Skip(start).Take(count))
        {
            var mapped = map(item);
            var size = Size(mapped);
            if (items.Count > 0 && characters + size > MaxPageCharacters)
            {
                break;
            }

            items.Add(mapped);
            characters += size;
        }

        return (start, items);
    }

    private static int Size(object? item) => item switch
    {
        StringItem s => s.Key.Length + s.Original.Length + (s.Translation?.Length ?? 0) + (s.Context?.Length ?? 0) + s.Placeholders.Sum(p => p.Length) + 40,
        GlossaryEntry e => e.Term.Length + e.Translation.Length + (e.Note?.Length ?? 0) + 40,
        _ => 40,
    };

    private (List<FileEntry> Entries, bool Truncated) Walk(string folder, string? pattern, bool recursive)
    {
        var entries = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(folder);
        var seen = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (++seen > MaxListedEntries)
                {
                    return (Sorted(entries), true);
                }

                var link = WorkingCopy.IsLink(child);
                var isFolder = !link && child is DirectoryInfo;
                if (isFolder && recursive)
                {
                    pending.Push(child.FullName);
                }

                if (pattern is null || (!isFolder && FileSystemName.MatchesSimpleExpression(pattern, child.Name)))
                {
                    entries.Add(new FileEntry(
                        session.Copy.Relative(child.FullName),
                        link ? "link" : isFolder ? "folder" : "file",
                        !link && child is FileInfo file ? file.Length : null));
                }
            }
        }

        return (Sorted(entries), false);
    }

    private static List<FileEntry> Sorted(List<FileEntry> entries)
    {
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private static bool LooksBinary(FileStream stream)
    {
        var buffer = new byte[8_000];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    private static SnippetResult Lines(string path, FileStream stream, int startLine, int lineCount)
    {
        var first = Math.Max(1, startLine);
        var wanted = Math.Clamp(lineCount, 1, MaxSnippetLines);
        var countAll = stream.Length <= MaxCountedFileSize;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        var number = 0;
        int? next = null;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (number < first)
            {
                continue;
            }

            if (next is null && number < first + wanted && text.Length < MaxPageCharacters)
            {
                var shown = line.Length > MaxLineLength ? string.Concat(line.AsSpan(0, MaxLineLength), " …[line cut]") : line;
                text.Append(number.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(shown).Append('\n');
                continue;
            }

            next ??= number;
            if (!countAll)
            {
                break;
            }
        }

        return new SnippetResult(path, stream.Length, "text", text.ToString(), countAll ? number : null, next, null);
    }

    private static SnippetResult Hex(string path, FileStream stream, long byteOffset, int byteCount)
    {
        var start = Math.Clamp(byteOffset, 0, stream.Length);
        var buffer = new byte[(int)Math.Min(Math.Clamp(byteCount, 1, MaxHexBytes), stream.Length - start)];
        stream.Position = start;
        stream.ReadExactly(buffer);
        var text = new StringBuilder();
        for (var row = 0; row < buffer.Length; row += 16)
        {
            var bytes = buffer.AsSpan(row, Math.Min(16, buffer.Length - row));
            text.Append((start + row).ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                text.Append(i < bytes.Length ? bytes[i].ToString("x2", CultureInfo.InvariantCulture) + " " : "   ");
            }

            text.Append(" |");
            foreach (var b in bytes)
            {
                text.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            }

            text.Append("|\n");
        }

        var end = start + buffer.Length;
        return new SnippetResult(path, stream.Length, "binary", text.ToString(), null, null, end < stream.Length ? end : null);
    }
}

public sealed record EngineCandidate(string Engine, string Name, string Marker);

/// <param name="Engine">Engine ID (<c>rpgmaker:mz</c>, <c>unity:mono</c>, <c>unknown</c>).</param>
/// <param name="Tool">The recipe tool that extracts and rebuilds the strings, or <c>null</c> (<paramref name="Problem"/> says why).</param>
public sealed record DetectResult(
    string Engine,
    string Name,
    string? Confidence,
    string? Marker,
    string? Executable,
    bool Ambiguous,
    IReadOnlyList<EngineCandidate> Candidates,
    string? Tool,
    string? ToolVersion,
    string? Problem);

/// <param name="Kind"><c>file</c>, <c>folder</c> or <c>link</c>.</param>
/// <param name="Size">Bytes of a file.</param>
public sealed record FileEntry(string Path, string Kind, long? Size);

/// <param name="Truncated">The walk stopped at <see cref="ToolkitTools.MaxListedEntries"/>; narrow the folder.</param>
public sealed record FileListResult(string Path, int Total, IReadOnlyList<FileEntry> Entries, int? NextOffset, bool Truncated);

/// <param name="Kind"><c>text</c> (numbered lines) or <c>binary</c> (hex dump).</param>
/// <param name="TotalLines">Lines in a text file (<c>null</c> for files over 32 MB).</param>
/// <param name="NextLine">First line not returned, or <c>null</c>.</param>
/// <param name="NextByteOffset">First byte not dumped, or <c>null</c>.</param>
public sealed record SnippetResult(string Path, long Size, string Kind, string Content, int? TotalLines, int? NextLine, long? NextByteOffset);

/// <param name="Translation">The stored translation, or <c>null</c>.</param>
/// <param name="MaxLength">Length limit in characters, or <c>null</c> = none known.</param>
/// <param name="Placeholders">Codes and tags to keep exactly as written.</param>
public sealed record StringItem(string Key, string Original, string? Translation, string? Context, int? MaxLength, IReadOnlyList<string> Placeholders);

/// <param name="TotalStrings">Strings the game has.</param>
/// <param name="Translated">Translations stored.</param>
/// <param name="Total">Strings matching the request (what <paramref name="NextOffset"/> pages through).</param>
/// <param name="UnknownKeys">Keys asked for that the game doesn't have.</param>
public sealed record StringPage(
    string Tool,
    string ToolVersion,
    int TotalStrings,
    int Files,
    int Translated,
    int Total,
    IReadOnlyList<StringItem> Strings,
    int? NextOffset,
    IReadOnlyList<string> UnknownKeys);

/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
public sealed record IssueText(string Severity, string Rule, string Message);

public sealed record KeyIssues(string Key, IReadOnlyList<IssueText> Issues);

public sealed record KeyIssue(string Key, string Severity, string Rule, string Message);

/// <param name="Stored">Translations stored by this call.</param>
/// <param name="Rejected">Translations refused for an error (not stored).</param>
/// <param name="Warnings">Stored translations with warnings.</param>
/// <param name="Translated">Translations in the store now.</param>
public sealed record PutTranslationsResult(
    int Stored,
    int Removed,
    IReadOnlyList<KeyIssues> Rejected,
    IReadOnlyList<KeyIssues> Warnings,
    IReadOnlyList<string> UnknownKeys,
    int Translated,
    int TotalStrings);

public sealed record ValidateResult(int Checked, int Errors, int Warnings, IReadOnlyList<KeyIssue> Issues, int? NextOffset, IReadOnlyList<string> UnknownKeys);

/// <param name="TotalEntries">Terms in the glossary.</param>
/// <param name="Total">Terms matching the search.</param>
public sealed record GlossaryPage(string? Language, string? Tone, int TotalEntries, int Total, IReadOnlyList<GlossaryEntry> Entries, int? NextOffset);

public sealed record PutGlossaryResult(int Added, int Replaced, int Removed, int TotalEntries, string? Tone);

/// <param name="Output">The build folder (absolute), laid out like the game folder.</param>
/// <param name="UnknownKeys">The first stored keys the game doesn't have.</param>
public sealed record RebuildToolResult(
    string Tool,
    string Output,
    IReadOnlyList<string> ChangedFiles,
    int Applied,
    int UnknownKeyCount,
    IReadOnlyList<string> UnknownKeys,
    IReadOnlyList<string> GeneratedFiles,
    string? InGameLanguage);
