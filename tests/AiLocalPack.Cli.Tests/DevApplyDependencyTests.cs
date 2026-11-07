using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>dev apply-files --dependency</c> (task 32): a whitelisted dependency installed with the files, without network (its archive is in the cache).</summary>
public sealed class DevApplyDependencyTests : IDisposable
{
    private const string GameFolder = "Steam/steamapps/common/Portal 2";

    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public DevApplyDependencyTests()
    {
        _steam = new SteamFixture(_dir);
        _steam.AddGame(620, "Portal 2", "100");
        _dir.File($"{GameFolder}/portal2.exe", Pe64())
            .File($"{GameFolder}/www/data/Map001.json", "map original")
            .File("files/www/data/Map001.json", "map ru");
        var archive = Archive();
        var asset = new DependencyAsset(
            new DependencyPlatform(Core.Storage.OsKind.Windows, "x64"),
            new Uri("https://github.com/example/loader/releases/download/v1/Loader_win_x64_1.0.zip"),
            archive.Length,
            Convert.ToHexStringLower(SHA256.HashData(archive)));
        var whitelist = new DependencyWhitelist(
            [new WhitelistedDependency("loader", "Loader", new Uri("https://example.org"), DependencyLayout.Root, [], [new DependencyVersion("1.0", [asset])])]);
        _dir.File("registry/index.json", RegistryIndexSerializer.Write(new RegistryIndex(DateTimeOffset.UnixEpoch, new Dictionary<Core.Games.GameId, RegistryGame>()) { Dependencies = whitelist }));
        _dir.File($"home/cache/dependencies/loader/1.0/{asset.Sha256[..16]}-Loader_win_x64_1.0.zip", archive);
    }

    public void Dispose() => _dir.Dispose();

    private static byte[] Pe64()
    {
        var bytes = new byte[0x80];
        "MZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x40);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x40));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x44), 0x8664);
        return bytes;
    }

    private static byte[] Archive()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("winhttp.dll").Open());
            writer.Write("proxy");
        }

        return memory.ToArray();
    }

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
                ["AILOCALPACK_INDEX_URL"] = _dir.Combine("registry"),
            },
            args);

    [Fact]
    public async Task Apply_files_installs_the_dependency_too()
    {
        // Act
        var result = await RunAsync("dev", "apply-files", "steam:620", _dir.Combine("files"), "--dependency", "loader@1.0");

        // Assert
        new { result.ExitCode, Dll = File.ReadAllText(_dir.Combine($"{GameFolder}/winhttp.dll")), Said = result.StdOut.Contains("Dependency Loader 1.0: 1 file installed.", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { ExitCode = 0, Dll = "proxy", Said = true });
    }

    [Fact]
    public async Task Uninstall_removes_the_dependency()
    {
        // Arrange
        (await RunAsync("dev", "apply-files", "steam:620", _dir.Combine("files"), "--dependency", "loader@1.0")).ExitCode.Should().Be(0);

        // Act
        var result = await RunAsync("dev", "uninstall", "steam:620");

        // Assert
        new { result.ExitCode, Gone = !File.Exists(_dir.Combine($"{GameFolder}/winhttp.dll")), Said = result.StdOut.Contains("removed dependency loader 1.0", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { ExitCode = 0, Gone = true, Said = true });
    }

    [Fact]
    public async Task Dependency_off_the_whitelist_is_refused()
    {
        // Act
        var result = await RunAsync("dev", "apply-files", "steam:620", _dir.Combine("files"), "--dependency", "melonloader@0.6.1");

        // Assert
        new { result.ExitCode, Said = result.StdErr.Contains("melonloader 0.6.1 is not on the registry's dependency whitelist (loader@1.0).", StringComparison.Ordinal), Untouched = File.ReadAllText(_dir.Combine($"{GameFolder}/www/data/Map001.json")) }
            .Should().BeEquivalentTo(new { ExitCode = 1, Said = true, Untouched = "map original" });
    }
}
