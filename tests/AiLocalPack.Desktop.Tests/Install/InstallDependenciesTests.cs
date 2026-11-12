using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>Packs that need a whitelisted executable (task 32): downloaded, hash checked, installed and removed with the pack.</summary>
public sealed class InstallDependenciesTests
{
    private const string Url = "https://github.com/example/loader/releases/download/v1/Loader_win_x64_1.0.zip";

    private static readonly PackDependency Loader = new("loader", "1.0");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    /// <summary>A release archive like BepInEx's: a proxy DLL in the game folder and the loader's core folder.</summary>
    private static byte[] Archive()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("winhttp.dll", "proxy"), ("Loader/core/Loader.dll", "core") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    private static byte[] Pe64()
    {
        var bytes = new byte[0x80];
        "MZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x40);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x40));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x44), 0x8664);
        return bytes;
    }

    private static DependencyWhitelist Whitelist(byte[] archive, string platform = "windows-x64")
    {
        DependencyPlatform.TryParse(platform, out var parsed);
        var asset = new DependencyAsset(parsed, new Uri(Url), archive.Length, Convert.ToHexStringLower(SHA256.HashData(archive)));
        return new DependencyWhitelist(
        [
            new WhitelistedDependency("loader", "Loader", new Uri("https://example.org/loader"), DependencyLayout.Root, ["Loader/LogOutput.log"], [new DependencyVersion("1.0", [asset])]),
        ]);
    }

    /// <summary>Game 10 as a 64-bit Windows build; <paramref name="packs"/> (all needing the loader) on a registry listing it.</summary>
    private static async Task<InstallBed> BedAsync(byte[]? served = null, string platform = "windows-x64", params string[] packs)
    {
        var archive = Archive();
        var bed = await InstallBed.CreateAsync(b =>
        {
            File.WriteAllBytes(Path.Combine(b.GameDir, "Game.exe"), Pe64());
            b.Registry.Dependencies = Whitelist(archive, platform);
            foreach (var id in packs.DefaultIfEmpty("lanterns"))
            {
                b.RecipePack(id, dependencies: [Loader]);
            }
        });
        bed.Services.GetRequiredService<DependencyDownloads>().Http.OnGet(Url, served ?? archive);
        return bed;
    }

    [Fact]
    public async Task Install_adds_the_dependency_files()
    {
        // Arrange
        using var bed = await BedAsync();
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Succeeded, bed.Read("winhttp.dll"), bed.Read("Loader/core/Loader.dll")).Should().Be((true, "proxy", "core"));
    }

    [Fact]
    public async Task Install_downloads_the_dependency_as_its_own_step()
    {
        // Arrange
        using var bed = await BedAsync();
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var steps = new List<InstallStep>();
        service.Changed += (_, _) =>
        {
            if (service.Current is { } current && !steps.Contains(current.Step))
            {
                steps.Add(current.Step);
            }
        };

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        steps.Take(2).Should().Equal(InstallStep.Download, InstallStep.Dependency);
    }

    [Fact]
    public async Task Log_says_the_dependency_came_from_its_official_release()
    {
        // Arrange
        using var bed = await BedAsync();
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        outcome.Log.Should().Contain(l => l.EndsWith($"Loader 1.0 (windows-x64): downloaded {Archive().Length} bytes from its official release (github.com), hash checked.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hash_mismatch_aborts_with_the_game_untouched()
    {
        // Arrange
        byte[] tampered = [.. Archive()];
        tampered[^1] ^= 0xFF;
        using var bed = await BedAsync(served: tampered);
        var before = Directory.EnumerateFiles(bed.GameDir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.Fetch, Directory.EnumerateFiles(bed.GameDir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes))
            .Should().BeEquivalentTo(((InstallErrorKind?)InstallErrorKind.DependencyFetch, (PackFetchProblem?)PackFetchProblem.HashMismatch, before));
    }

    [Fact]
    public async Task Hash_mismatch_leaves_the_installed_pack_in_place()
    {
        // Arrange: the installed pack needs nothing; the new one needs the loader, served tampered (same size)
        using var bed = await InstallBed.CreateAsync(b =>
        {
            File.WriteAllBytes(Path.Combine(b.GameDir, "Game.exe"), Pe64());
            b.Registry.Dependencies = Whitelist(Archive());
            b.RecipePack("lanterns").RecipePack("torches", dependencies: [Loader]);
        });
        byte[] tampered = [.. Archive()];
        tampered[0] ^= 0xFF;
        bed.Services.GetRequiredService<DependencyDownloads>().Http.OnGet(Url, tampered);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "torches"), Ct);

        // Assert
        (outcome.Error?.Fetch, bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)?.PackId).Should().Be((PackFetchProblem.HashMismatch, "lanterns"));
    }

    [Fact]
    public async Task Pack_needing_a_dependency_off_the_whitelist_is_refused_untouched()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", dependencies: [new PackDependency("melonloader", "0.6.1")]));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Error?.Kind, bed.Services.GetRequiredService<DependencyDownloads>().Http.Requests.Count, bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal))
            .Should().Be((InstallErrorKind.DependencyUnknown, 0, false));
    }

    [Fact]
    public async Task Dependency_without_a_release_for_the_game_build_is_refused()
    {
        // Arrange: the whitelist only has a Linux build; the game is a Windows build
        using var bed = await BedAsync(platform: "linux-x64");
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.Platform).Should().Be((InstallErrorKind.DependencyPlatform, "windows-x64"));
    }

    [Fact]
    public async Task Failed_replacement_removes_the_dependency_kept_for_it()
    {
        // Arrange: a game file broken after the first install makes the second pack's rebuild fail once the first is gone
        using var bed = await BedAsync(packs: ["lanterns", "torches"]);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), Ct);
        File.WriteAllText(Path.Combine(bed.GameDir, "www", "data", "Map001.json"), "{");

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "torches"), Ct);

        // Assert
        (outcome.Error?.Kind, bed.Services.GetRequiredService<InstalledPackStore>().DependenciesOf(InstallBed.Alpha).Count, File.Exists(Path.Combine(bed.GameDir, "winhttp.dll")))
            .Should().Be((InstallErrorKind.Recipe, 0, false));
    }
}
