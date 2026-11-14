using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Steam;

/// <summary>How a store lookup ended.</summary>
public enum SteamStoreLookup
{
    /// <summary>The store answered with the app's languages (maybe none).</summary>
    Found,

    /// <summary>The store doesn't know the app (removed, region-locked, a tool): nothing to show.</summary>
    NotFound,

    /// <summary>Too many requests: stop asking until later.</summary>
    RateLimited,

    /// <summary>Offline, a timeout, a server error or a reply that can't be read: try again later.</summary>
    Failed,
}

/// <summary>A store lookup's answer; <see cref="Languages"/> is set when <see cref="Lookup"/> is <see cref="SteamStoreLookup.Found"/>.</summary>
public sealed record SteamStoreLanguagesResult(SteamStoreLookup Lookup, IReadOnlyList<OfficialLanguage>? Languages = null, string? Problem = null);

/// <summary>The languages of a Steam app from the store, for the games the client's app info doesn't hold.</summary>
public interface ISteamStoreLanguages
{
    /// <summary>Never throws for network trouble (it becomes <see cref="SteamStoreLookup.Failed"/>); cancelling throws.</summary>
    Task<SteamStoreLanguagesResult> GetAsync(uint appId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The store's public app details, <c>store.steampowered.com/api/appdetails?appids=&lt;id&gt;&amp;filters=basic&amp;l=english</c>
/// (no key; about 200 requests in 5 minutes): <c>supported_languages</c> is one HTML string of English language names,
/// those with full audio marked <c>&lt;strong&gt;*&lt;/strong&gt;</c>, then <c>&lt;br&gt;</c> and the legend. The store
/// doesn't tell interface from subtitles, so each listed language counts as interface.
/// </summary>
public sealed partial class SteamStoreLanguages(HttpClient http, string baseUrl = SteamStoreLanguages.StoreUrl) : ISteamStoreLanguages
{
    public const string StoreUrl = "https://store.steampowered.com";

    public async Task<SteamStoreLanguagesResult> GetAsync(uint appId, CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{baseUrl.TrimEnd('/')}/api/appdetails?appids={appId}&filters=basic&l=english");
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                return new(SteamStoreLookup.RateLimited, Problem: $"HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(SteamStoreLookup.Failed, Problem: $"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return Parse(appId, body);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new(SteamStoreLookup.Failed, Problem: e.Message);
        }
    }

    /// <summary>Reads an app details reply. Pure.</summary>
    public static SteamStoreLanguagesResult Parse(uint appId, ReadOnlySpan<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(body.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var app)
                || app.ValueKind != JsonValueKind.Object)
            {
                return new(SteamStoreLookup.Failed, Problem: "the reply doesn't name the app");
            }

            if (!app.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                return new(SteamStoreLookup.NotFound);
            }

            var text = app.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("supported_languages", out var languages) && languages.ValueKind == JsonValueKind.String
                ? languages.GetString()
                : null;
            return new(SteamStoreLookup.Found, ParseLanguages(text));
        }
        catch (JsonException e)
        {
            return new(SteamStoreLookup.Failed, Problem: e.Message);
        }
    }

    /// <summary>
    /// <c>English&lt;strong&gt;*&lt;/strong&gt;, French, Simplified Chinese&lt;br&gt;&lt;strong&gt;*&lt;/strong&gt;languages with full audio support</c>
    /// → en (audio), fr, zh-Hans. Unknown names are skipped. Pure.
    /// </summary>
    public static IReadOnlyList<OfficialLanguage> ParseLanguages(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var list = LineBreak().Split(text, 2)[0];
        var result = new List<OfficialLanguage>();
        foreach (var item in list.Split(','))
        {
            var audio = item.Contains('*', StringComparison.Ordinal);
            var name = WebUtility.HtmlDecode(Tag().Replace(item, string.Empty)).Replace("*", string.Empty, StringComparison.Ordinal).Trim();
            if (SteamLanguageNames.FromEnglishName(name) is { } tag && result.All(l => l.Tag != tag))
            {
                result.Add(new OfficialLanguage(tag, Interface: true, Subtitles: false, FullAudio: audio));
            }
        }

        return result;
    }

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();
}
