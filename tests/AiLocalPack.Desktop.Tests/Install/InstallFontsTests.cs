using System.Security.Cryptography;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Toolkit.Fonts;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>Installs whose translation the game's Latin-only font can't draw (task 31): the open font is swapped in.</summary>
public sealed class InstallFontsTests
{
    private const string NotoFile = "www/fonts/ailocalpack-NotoSans-Regular.ttf";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    [Fact]
    public async Task A_cyrillic_pack_makes_the_game_draw_its_text_with_noto_sans()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", prefix: "РУ ", fonts: ["noto-sans"]));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Succeeded,
            bed.Read("www/fonts/gamefont.css").Contains("url(\"ailocalpack-NotoSans-Regular.ttf\")", StringComparison.Ordinal),
            OpenTypeFont.Read(Path.Combine(bed.GameDir, NotoFile)).FamilyName)
            .Should().Be((true, true, "Noto Sans"));
    }

    [Fact]
    public async Task The_log_says_which_font_was_swapped_and_why()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", prefix: "РУ "));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        outcome.Log.Should().Contain(line => line.EndsWith("Fonts: «Fixture Sans» lacks letters of the translation (missing: 2; Р, У); replaced with Noto Sans (open font).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_latin_translation_leaves_the_game_font_alone()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Log.Any(l => l.EndsWith("Fonts: the game's fonts have every letter of the translation.", StringComparison.Ordinal)), File.Exists(Path.Combine(bed.GameDir, NotoFile)))
            .Should().Be((true, false));
    }

    [Fact]
    public async Task Uninstall_removes_the_swapped_font_and_restores_the_game_font()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", prefix: "РУ "));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var before = Snapshot(bed.GameDir);
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Act
        var outcome = await service.UninstallAsync(bed.Game, Ct);

        // Assert
        (outcome.Succeeded, Snapshot(bed.GameDir)).Should().BeEquivalentTo((true, before));
    }

    [Fact]
    public async Task A_renpy_game_gets_the_font_mapped_in_its_translation()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", prefix: "РУ "), renPy: true);
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        var record = bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!;
        (record.Files.Select(f => f.Path).Where(p => p.Contains("ailocalpack_fonts", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList(),
            record.GeneratedFiles.Contains("game/tl/ailocalpack_ru/ailocalpack_fonts.rpyc"))
            .Should().BeEquivalentTo((
                new List<string>
                {
                    "game/tl/ailocalpack_ru/ailocalpack_fonts.rpy",
                    "game/tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Bold.ttf",
                    "game/tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Regular.ttf",
                },
                true));
    }

    [Fact]
    public async Task A_needed_font_that_cant_be_downloaded_fails_the_install_and_touches_nothing()
    {
        // Arrange
        var font = FontWriter.TrueType("Cyrillic Test", [.. FontWriter.Ascii, .. Enumerable.Range(0x400, 0x100)]);
        const string url = "https://fonts.example.org/CyrillicTest-Regular.ttf";
        using var bed = await InstallBed.CreateAsync(
            b => b.RecipePack("lanterns", prefix: "РУ "),
            services => services.AddSingleton(new FontWhitelist([
                new WhitelistFont("cyrillic-test", "Cyrillic Test", "OFL-1.1", [], true, [
                    new WhitelistFontFile(FontFace.Regular, "CyrillicTest-Regular.ttf", font.Length, Convert.ToHexStringLower(SHA256.HashData(font)), new Uri(url)),
                ]),
            ])));
        bed.Services.GetRequiredService<FontDownloads>().Http.On(HttpMethod.Get, url, _ => throw new HttpRequestException("No route to host"));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var before = Snapshot(bed.GameDir);

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.FontName, outcome.Error?.CanRetry, Snapshot(bed.GameDir))
            .Should().BeEquivalentTo(((InstallErrorKind?)InstallErrorKind.Font, "Cyrillic Test", (bool?)true, before));
    }

    /// <summary>Every file of the folder with its hash.</summary>
    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);
}
