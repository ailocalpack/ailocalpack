using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Requests;

/// <summary>What a translation request asks for: a game into a language (<c>steam:620/ru</c>).</summary>
public sealed record RequestKey(GameId Game, string Language)
{
    public override string ToString() => $"{Game}/{Language}";

    /// <summary>Parses <c>&lt;game id&gt;/&lt;language&gt;</c> with a supported language.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out RequestKey? key)
    {
        key = null;
        var parts = text?.Split('/');
        if (parts is not { Length: 2 } || !GameId.TryParse(parts[0], out var game) || !GameLanguages.Tags.Contains(parts[1], StringComparer.Ordinal))
        {
            return false;
        }

        key = new RequestKey(game, parts[1]);
        return true;
    }
}

/// <summary>
/// The first line of a request's Discussion, which ties it to the game and language (task 74, decided):
/// <c>&lt;!-- ailocalpack-request steam:620/ru --&gt;</c>, an HTML comment GitHub doesn't render. The category
/// <c>Requests</c> is open (players create the Discussions), so two may name the same request: the collector counts
/// them together and closes the newer ones.
/// </summary>
public static partial class RequestDiscussionMarker
{
    public static string Format(RequestKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"<!-- ailocalpack-request {key} -->";
    }

    /// <summary>The request a Discussion body names at its very start, or <c>false</c>.</summary>
    public static bool TryParse(string? body, [NotNullWhen(true)] out RequestKey? key)
    {
        key = null;
        return body is not null && MarkerPattern().Match(body) is { Success: true } match && RequestKey.TryParse(match.Groups["key"].Value, out key);
    }

    [GeneratedRegex(@"\A\s*<!--\s*ailocalpack-request\s+(?<key>[^\s]+)\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}

/// <summary>The title and body of a request's Discussion (English: players of every language share it).</summary>
public static class RequestDiscussionText
{
    /// <summary>"Neon Courier → Русский · steam:620/ru" (the key makes it findable by title search).</summary>
    public static string Title(RequestKey key, string gameName)
    {
        ArgumentNullException.ThrowIfNull(key);
        var name = string.IsNullOrWhiteSpace(gameName) ? key.Game.ToString() : gameName.Trim().Replace('\n', ' ');
        return $"{name} → {LanguageNames.Native(key.Language)} · {key}";
    }

    public static string Body(RequestKey key, string gameName)
    {
        ArgumentNullException.ThrowIfNull(key);
        var store = key.Game.Kind == GameIdKind.Steam
            ? $" ([Steam](https://store.steampowered.com/app/{key.Game.Value}/))"
            : " (a game added by its folder: only the same files match)";
        return $"""
            {RequestDiscussionMarker.Format(key)}
            Players ask for a translation of **{gameName.Trim()}**{store} into **{LanguageNames.English(key.Language)}** (`{key.Language}`).

            - 👍 this post to add your voice; the most asked requests are usually taken first.
            - Have the game and an AI agent (Claude Code or Codex)? Open the game in {AppInfo.DisplayName}, tab *Create with AI*: the start form offers to take this request, and the app shows your progress here.
            - Once a translation is published to the registry, this Discussion is closed and everyone who asked is told in the app.

            """;
    }
}

/// <summary>How far a translator's claim on a request got.</summary>
public enum RequestClaimState
{
    /// <summary>The generation runs (or is paused / interrupted and may go on).</summary>
    Working,

    /// <summary>The translation is done locally; publishing comes next.</summary>
    Built,

    /// <summary>The pack was sent to the registry (its PR is open).</summary>
    Published,

    /// <summary>The translator cancelled: the claim no longer counts.</summary>
    Stopped,
}

/// <summary>A translator's claim on a request, as their app writes it into the Discussion.</summary>
/// <param name="Stage">The generation stage while <see cref="RequestClaimState.Working"/>.</param>
/// <param name="Percent">The job's progress, 0–100.</param>
/// <param name="Agent">The agent's adapter ID (<c>claude</c>, <c>codex</c>).</param>
public sealed record RequestClaim(
    RequestKey Key,
    RequestClaimState State,
    GenerationStage? Stage = null,
    int Percent = 0,
    string? Agent = null,
    GenerationQuality? Quality = null);

/// <summary>
/// The comment format of a claim (task 74), shared with the registry's Action (<c>requests collect</c>), which parses
/// exactly this:
/// <code>
/// &lt;!-- ailocalpack-claim {"v":1,"game":"steam:620","language":"ru","state":"working","stage":"translate","percent":45,"agent":"claude","quality":"balanced"} --&gt;
/// 🛠 **Translating** · stage Translate · 45% · with Claude Code (Balanced)
///
/// &lt;sub&gt;Sent from AI Local Pack; updated at every stage.&lt;/sub&gt;
/// </code>
/// The translator's app edits its one comment as the job goes on (its last edit = the last news). Only the marker
/// counts, only at the very start; a newer <c>v</c> isn't read.
/// </summary>
public static partial class RequestClaimComment
{
    public const int CurrentVersion = 1;

    public static string Format(RequestClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var marker = JsonSerializer.Serialize(
            new ClaimMarkerJson(
                CurrentVersion,
                claim.Key.Game.ToString(),
                claim.Key.Language,
                StateText(claim.State),
                claim.Stage is { } stage ? StageText(stage) : null,
                Math.Clamp(claim.Percent, 0, 100),
                claim.Agent,
                claim.Quality?.ToString().ToLowerInvariant()),
            RequestFormatJsonContext.Default.ClaimMarkerJson);
        var text = new StringBuilder();
        text.Append("<!-- ailocalpack-claim ").Append(marker).Append(" -->\n");
        text.Append(claim.State switch
        {
            RequestClaimState.Working => string.Create(CultureInfo.InvariantCulture,
                $"🛠 **Translating** · stage {claim.Stage ?? GenerationStage.Setup} · {Math.Clamp(claim.Percent, 0, 100)}%"),
            RequestClaimState.Built => "✅ **Translated**: publishing comes next",
            RequestClaimState.Published => "📦 **Published**: waiting for the registry's checks",
            _ => "⏹ **Stopped**: the request is open for others again",
        });
        if (claim.State != RequestClaimState.Stopped && claim.Agent is { Length: > 0 } agent)
        {
            text.Append(" · with ").Append(AgentName(agent));
            if (claim.Quality is { } quality)
            {
                text.Append(" (").Append(quality).Append(')');
            }
        }

        text.Append("\n\n<sub>Sent from ").Append(AppInfo.DisplayName).Append("; updated at every stage.</sub>\n");
        return text.ToString();
    }

    /// <summary>The claim of a comment, or <c>false</c> for any other comment.</summary>
    public static bool TryParse(string? body, [NotNullWhen(true)] out RequestClaim? claim)
    {
        claim = null;
        if (body is null || MarkerPattern().Match(body) is not { Success: true } match)
        {
            return false;
        }

        ClaimMarkerJson? json;
        try
        {
            json = JsonSerializer.Deserialize(match.Groups["json"].Value, RequestFormatJsonContext.Default.ClaimMarkerJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (json is not { V: CurrentVersion }
            || !RequestKey.TryParse($"{json.Game}/{json.Language}", out var key)
            || !TryState(json.State, out var state))
        {
            return false;
        }

        GenerationStage? stage = TryStage(json.Stage, out var s) ? s : null;
        GenerationQuality? quality = Enum.TryParse<GenerationQuality>(json.Quality, ignoreCase: true, out var q) && Enum.IsDefined(q) ? q : null;
        claim = new RequestClaim(key, state, stage, Math.Clamp(json.Percent, 0, 100), json.Agent is { Length: > 0 } a ? a : null, quality);
        return true;
    }

    public static string StateText(RequestClaimState state) => state switch
    {
        RequestClaimState.Working => "working",
        RequestClaimState.Built => "built",
        RequestClaimState.Published => "published",
        _ => "stopped",
    };

    public static bool TryState(string? text, out RequestClaimState state)
    {
        (var ok, state) = text switch
        {
            "working" => (true, RequestClaimState.Working),
            "built" => (true, RequestClaimState.Built),
            "published" => (true, RequestClaimState.Published),
            "stopped" => (true, RequestClaimState.Stopped),
            _ => (false, RequestClaimState.Stopped),
        };
        return ok;
    }

    public static string StageText(GenerationStage stage) => stage.ToString().ToLowerInvariant();

    public static bool TryStage(string? text, out GenerationStage stage) =>
        Enum.TryParse(text, ignoreCase: true, out stage) && Enum.IsDefined(stage) && !int.TryParse(text, CultureInfo.InvariantCulture, out _);

    private static string AgentName(string agent) => agent switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => agent.Replace('`', '\'').Replace('\n', ' '),
    };

    [GeneratedRegex(@"\A\s*<!--\s*ailocalpack-claim\s+(?<json>\{[^\n]*?\})\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}

/// <summary>When a claim counts and what a note may hold (task 74). Pure.</summary>
public static class RequestRules
{
    /// <summary>A claim without news (its comment unchanged) for this long no longer counts: the request is open again.</summary>
    public static readonly TimeSpan ClaimLifetime = TimeSpan.FromDays(14);

    /// <summary>The longest note kept (cut with "…").</summary>
    public const int MaxNoteLength = 1000;

    /// <summary>A claim said at <paramref name="saidAt"/> still holds the request at <paramref name="now"/>.</summary>
    public static bool IsLive(RequestClaimState state, DateTimeOffset saidAt, DateTimeOffset now) =>
        state != RequestClaimState.Stopped && now - saidAt <= ClaimLifetime;

    /// <summary>The player's note trimmed, HTML comments defused (no fake markers), cut at <see cref="MaxNoteLength"/>; <c>null</c> when empty.</summary>
    public static string? CleanNote(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim()
            .Replace("<!--", "&lt;!--", StringComparison.Ordinal).Replace("-->", "--&gt;", StringComparison.Ordinal);
        return clean.Length <= MaxNoteLength ? clean : clean[..(MaxNoteLength - 1)].TrimEnd() + "…";
    }
}

internal sealed record ClaimMarkerJson(
    int V,
    string? Game,
    string? Language,
    string? State,
    string? Stage,
    int Percent,
    string? Agent,
    string? Quality);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClaimMarkerJson))]
internal sealed partial class RequestFormatJsonContext : JsonSerializerContext;
