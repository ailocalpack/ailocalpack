using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Tests.Steam;

public sealed class SteamAppInfoTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(SteamAppInfoWriter writer) => writer.WriteTo(_dir.Path);

    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    public void Reads_interface_subtitles_and_audio_per_language_in_every_format(int version)
    {
        // Arrange
        var file = Write(new SteamAppInfoWriter(version).AddApp(
            396750,
            "EVERSPACE",
            new AppInfoLanguage("english", Subtitles: true, FullAudio: true),
            new AppInfoLanguage("russian", Subtitles: true)));

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [396750]);

        // Assert
        read.Problem.Should().BeNull();
        read.Apps[396750].Should().Equal(
            new OfficialLanguage("en", Interface: true, Subtitles: true, FullAudio: true),
            new OfficialLanguage("ru", Interface: true, Subtitles: true, FullAudio: false));
    }

    [Fact]
    public void Maps_steam_api_names_to_language_tags_and_skips_unknown_ones()
    {
        // Arrange
        var file = Write(new SteamAppInfoWriter().AddApp(
            1942280,
            "Brotato",
            new AppInfoLanguage("schinese"),
            new AppInfoLanguage("latam"),
            new AppInfoLanguage("koreana"),
            new AppInfoLanguage("sc_schinese")));

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [1942280]);

        // Assert
        read.Apps[1942280].Select(l => l.Tag).Should().Equal("zh-Hans", "es-419", "ko");
    }

    [Fact]
    public void Reads_only_the_asked_apps()
    {
        // Arrange
        var file = Write(new SteamAppInfoWriter()
            .AddApp(10, "One", new AppInfoLanguage("english"))
            .AddApp(20, "Two", new AppInfoLanguage("german"))
            .AddApp(30, "Three", new AppInfoLanguage("french")));

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [20, 99]);

        // Assert
        read.Apps.Keys.Should().Equal(20u);
    }

    [Fact]
    public void Falls_back_to_the_older_language_list_as_interface_only()
    {
        // Arrange
        var file = Write(new SteamAppInfoWriter().AddAppWithLanguageList(50, "english", "russian"));

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [50]);

        // Assert
        read.Apps[50].Should().Equal(new OfficialLanguage("en", true, false, false), new OfficialLanguage("ru", true, false, false));
    }

    [Fact]
    public void Leaves_out_an_app_without_language_data()
    {
        // Arrange
        var file = Write(new SteamAppInfoWriter().AddAppWithoutLanguages(60));

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [60]);

        // Assert
        read.Apps.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_file_reports_the_problem_without_throwing()
    {
        // Arrange
        var file = _dir.Combine("appcache/appinfo.vdf");

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [10]);

        // Assert
        read.Apps.Should().BeEmpty();
        read.Problem.Should().NotBeNull();
    }

    [Fact]
    public void An_unknown_format_reports_the_problem_without_throwing()
    {
        // Arrange
        var file = _dir.Combine("appinfo.vdf");
        File.WriteAllBytes(file, [0x30, 0x44, 0x56, 0x07, 1, 0, 0, 0, 0, 0, 0, 0]);

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [10]);

        // Assert
        read.Problem.Should().Contain("unknown appinfo.vdf format");
    }

    [Fact]
    public void A_file_cut_short_keeps_the_apps_read_before_the_break()
    {
        // Arrange
        var bytes = new SteamAppInfoWriter(28)
            .AddApp(10, "One", new AppInfoLanguage("english"))
            .AddApp(20, "Two", new AppInfoLanguage("german"))
            .ToBytes();
        var file = _dir.Combine("appinfo.vdf");
        File.WriteAllBytes(file, bytes[..^20]);

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [10, 20]);

        // Assert
        read.Apps.Keys.Should().Equal(10u);
        read.Problem.Should().NotBeNull();
    }

    [Fact]
    public void A_broken_string_table_offset_reports_the_problem_without_throwing()
    {
        // Arrange
        var bytes = new SteamAppInfoWriter(29).AddApp(10, "One", new AppInfoLanguage("english")).ToBytes();
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 8);
        var file = _dir.Combine("appinfo.vdf");
        File.WriteAllBytes(file, bytes);

        // Act
        var read = SteamAppInfo.ReadLanguages(file, [10]);

        // Assert
        read.Apps.Should().BeEmpty();
        read.Problem.Should().Contain("string table");
    }
}
