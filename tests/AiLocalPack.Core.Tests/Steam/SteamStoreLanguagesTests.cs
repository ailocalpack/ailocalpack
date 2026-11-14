using System.Net;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Tests.Steam;

public sealed class SteamStoreLanguagesTests
{
    private const string Url = "https://store.test/api/appdetails?appids=413150&filters=basic&l=english";

    private static string Reply(string languages) =>
        "{\"413150\":{\"success\":true,\"data\":{\"type\":\"game\",\"supported_languages\":\"" + languages + "\"}}}";

    [Fact]
    public void Parses_the_languages_and_marks_full_audio()
    {
        // Arrange
        var text = "English<strong>*</strong>, French, Simplified Chinese<br><strong>*</strong>languages with full audio support";

        // Act
        var languages = SteamStoreLanguages.ParseLanguages(text);

        // Assert
        languages.Should().Equal(
            new OfficialLanguage("en", Interface: true, Subtitles: false, FullAudio: true),
            new OfficialLanguage("fr", true, false, false),
            new OfficialLanguage("zh-Hans", true, false, false));
    }

    [Fact]
    public void Maps_regional_store_names_and_skips_unknown_ones()
    {
        // Arrange
        var text = "Spanish - Latin America, Portuguese - Brazil, Klingon, Spanish - Spain";

        // Act
        var languages = SteamStoreLanguages.ParseLanguages(text);

        // Assert
        languages.Select(l => l.Tag).Should().Equal("es-419", "pt-BR", "es-ES");
    }

    [Fact]
    public void An_app_the_store_does_not_know_is_not_found()
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes("""{"413150":{"success":false}}""");

        // Act
        var result = SteamStoreLanguages.Parse(413150, body);

        // Assert
        result.Lookup.Should().Be(SteamStoreLookup.NotFound);
    }

    [Fact]
    public void A_reply_that_is_not_json_failed()
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes("<html>busy</html>");

        // Act
        var result = SteamStoreLanguages.Parse(413150, body);

        // Assert
        result.Lookup.Should().Be(SteamStoreLookup.Failed);
    }

    [Fact]
    public async Task Asks_the_store_for_the_app_and_reads_its_languages()
    {
        // Arrange
        var http = new FakeHttpHandler().OnGet(Url, Reply("English, Russian"));
        var store = new SteamStoreLanguages(http.CreateClient(), "https://store.test");

        // Act
        var result = await store.GetAsync(413150, TestContext.Current.CancellationToken);

        // Assert
        result.Languages!.Select(l => l.Tag).Should().Equal("en", "ru");
    }

    [Fact]
    public async Task Too_many_requests_is_rate_limited()
    {
        // Arrange
        var http = new FakeHttpHandler().OnGet(Url, HttpStatusCode.TooManyRequests);
        var store = new SteamStoreLanguages(http.CreateClient(), "https://store.test");

        // Act
        var result = await store.GetAsync(413150, TestContext.Current.CancellationToken);

        // Assert
        result.Lookup.Should().Be(SteamStoreLookup.RateLimited);
    }

    [Fact]
    public async Task A_server_error_failed()
    {
        // Arrange
        var http = new FakeHttpHandler().OnGet(Url, HttpStatusCode.BadGateway);
        var store = new SteamStoreLanguages(http.CreateClient(), "https://store.test");

        // Act
        var result = await store.GetAsync(413150, TestContext.Current.CancellationToken);

        // Assert
        result.Lookup.Should().Be(SteamStoreLookup.Failed);
    }

    [Fact]
    public async Task A_network_error_failed_without_throwing()
    {
        // Arrange
        var http = new FakeHttpHandler().On(HttpMethod.Get, Url, _ => throw new HttpRequestException("offline"));
        var store = new SteamStoreLanguages(http.CreateClient(), "https://store.test");

        // Act
        var result = await store.GetAsync(413150, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new SteamStoreLanguagesResult(SteamStoreLookup.Failed, Problem: "offline"));
    }
}
