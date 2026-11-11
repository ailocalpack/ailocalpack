using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Ratings;

/// <summary>What a player says about a pack they played with.</summary>
public enum PackVerdict
{
    Works,
    DoesNotWork,
}

/// <summary>
/// A works / doesn't-work report (task 63): the pack and revision, the game build the player played, and an optional
/// comment. Posted as a comment in the pack's Discussion (<see cref="PackReportComment"/>); the registry's scheduled
/// Action (task 64) parses it back to count confirmations.
/// </summary>
/// <param name="Build">The game's build: the store build ID, or <c>files:&lt;8 hex&gt;</c> for a file-hash version; <c>null</c> when unknown.</param>
/// <param name="Text">The player's own words, or <c>null</c>.</param>
/// <param name="App">"0.1.0 macos" (app version and OS), shown in small print; <c>null</c> to leave out.</param>
public sealed record PackReport(
    PackVerdict Verdict,
    GameId Game,
    string Language,
    string PackId,
    int Revision,
    string? Build,
    string? Text = null,
    string? App = null)
{
    /// <summary>The longest comment kept (longer text is cut with "…"); a report is a few lines, not an essay.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>The same pack and revision (a later report of the player replaces the earlier one).</summary>
    public bool IsAbout(GameId game, string language, string packId, int revision) =>
        Game == game && Language == language && PackId == packId && Revision == revision;

    /// <summary>
    /// A short name of <paramref name="version"/> for the report: the build ID, or <c>files:</c> + the first 8 hex digits
    /// of SHA-256 over the sorted "path hash" lines (the game page's version chip).
    /// </summary>
    public static string? BuildOf(GameVersion? version) => version switch
    {
        null => null,
        { BuildId: { } build } => build,
        { FileHashes: { } files } => "files:" + FilesHash(files),
        _ => null,
    };

    /// <summary>The first 8 hex digits of SHA-256 over the sorted "path hash" lines.</summary>
    public static string FilesHash(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var text = new StringBuilder();
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            text.Append(path).Append(' ').Append(hash).Append('\n');
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..8];
    }
}

/// <summary>
/// The comment format of a report, shared with the registry's Action (task 64), which must parse exactly this. The
/// first line is a machine-readable marker, an HTML comment GitHub doesn't render:
/// <code>
/// &lt;!-- ailocalpack-report {"v":1,"verdict":"works","game":"steam:620","language":"ru","pack":"full","revision":3,"build":"9087113"} --&gt;
/// ✅ **Works** · «full» rev 3 · game build `9087113`
///
/// &gt; the player's text, quoted
///
/// &lt;sub&gt;Sent from AI Local Pack 0.1.0 macos.&lt;/sub&gt;
/// </code>
/// <c>verdict</c> is <c>works</c> or <c>doesNotWork</c>; <c>build</c> is left out when unknown. The visible part is in
/// English (the Discussion is shared by players of every language) and is never parsed: only the marker counts, and
/// only at the very start of the comment. A newer <c>v</c> isn't read (an older Action skips it).
/// </summary>
public static partial class PackReportComment
{
    public const int CurrentVersion = 1;

    private const string MarkerName = "ailocalpack-report";

    public static string Format(PackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var marker = JsonSerializer.Serialize(
            new ReportMarkerJson(CurrentVersion, Verdict(report.Verdict), report.Game.ToString(), report.Language, report.PackId, report.Revision, report.Build),
            PackReportJsonContext.Default.ReportMarkerJson);

        var text = new StringBuilder();
        text.Append("<!-- ").Append(MarkerName).Append(' ').Append(marker).Append(" -->\n");
        text.Append(report.Verdict == PackVerdict.Works ? "✅ **Works**" : "❌ **Doesn't work**");
        text.Append(" · «").Append(Plain(report.PackId)).Append("» rev ").Append(report.Revision.ToString(CultureInfo.InvariantCulture));
        if (report.Build is { Length: > 0 } build)
        {
            text.Append(" · game build `").Append(Plain(build)).Append('`');
        }

        text.Append('\n');
        if (Clean(report.Text) is { } words)
        {
            text.Append('\n');
            foreach (var line in words.Split('\n'))
            {
                text.Append("> ").Append(line).Append('\n');
            }
        }

        if (report.App is { Length: > 0 } app)
        {
            text.Append("\n<sub>Sent from ").Append(Core.AppInfo.DisplayName).Append(' ').Append(Plain(app)).Append(".</sub>\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// The report of a comment, or <c>false</c> for any other comment (no marker at its start, a newer version, a field
    /// missing or wrong). The player's text isn't read back.
    /// </summary>
    public static bool TryParse(string? body, [NotNullWhen(true)] out PackReport? report)
    {
        report = null;
        if (body is null || MarkerPattern().Match(body) is not { Success: true } match)
        {
            return false;
        }

        ReportMarkerJson? json;
        try
        {
            json = JsonSerializer.Deserialize(match.Groups["json"].Value, PackReportJsonContext.Default.ReportMarkerJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (json is not { V: CurrentVersion, Revision: > 0 }
            || !GameId.TryParse(json.Game, out var game)
            || json.Language is not { Length: > 0 } language
            || json.Pack is not { Length: > 0 } pack)
        {
            return false;
        }

        PackVerdict verdict;
        switch (json.Verdict)
        {
            case "works":
                verdict = PackVerdict.Works;
                break;
            case "doesNotWork":
                verdict = PackVerdict.DoesNotWork;
                break;
            default:
                return false;
        }

        report = new PackReport(verdict, game, language, pack, json.Revision, json.Build is { Length: > 0 } build ? build : null);
        return true;
    }

    /// <summary>The player's text trimmed, line ends unified, cut at <see cref="PackReport.MaxTextLength"/>; <c>null</c> when empty.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

        // An HTML comment in the player's text can't hide the rest of the comment or fake a marker.
        clean = clean.Replace("<!--", "&lt;!--", StringComparison.Ordinal).Replace("-->", "--&gt;", StringComparison.Ordinal);
        return clean.Length <= PackReport.MaxTextLength ? clean : clean[..(PackReport.MaxTextLength - 1)].TrimEnd() + "…";
    }

    private static string Verdict(PackVerdict verdict) => verdict == PackVerdict.Works ? "works" : "doesNotWork";

    /// <summary>A value shown in the visible line: no backticks or line breaks that would break its formatting.</summary>
    private static string Plain(string value) => value.Replace("`", "'", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    [GeneratedRegex(@"\A\s*<!--\s*ailocalpack-report\s+(?<json>\{[^\n]*?\})\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}

internal sealed record ReportMarkerJson(
    int V,
    string? Verdict,
    string? Game,
    string? Language,
    string? Pack,
    int Revision,
    string? Build);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportMarkerJson))]
internal sealed partial class PackReportJsonContext : JsonSerializerContext;
