using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RenPy;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>
/// A real install end to end: Steam game 10 "Alpha" (build 1000) holding the synthetic RPG Maker MZ game, Steam game 20
/// "Beta" (no files), target Russian, and a local registry whose packs are real: a gzip string table translating every
/// string of the game ("RU " + the original) and a manifest with the hashes of the files it touches.
/// </summary>
internal sealed class InstallBed : IDisposable
{
    public static readonly GameId Alpha = GameId.Steam(10);

    private readonly TestRegistry _registry = new();

    /// <summary>The registry (packs added with <see cref="RecipePack"/>), the app's services, the game folder.</summary>
    public TestRegistry Registry => _registry;

    public ServiceProvider Services { get; private set; } = null!;

    private IRecipeTool _tool = new RpgMakerRecipe();

    public string GameDir { get; private set; } = string.Empty;

    /// <summary>A file the packs translate (relative to the game folder).</summary>
    public const string SystemFile = "data/System.json";

    /// <param name="renPy">Game 10 is the synthetic Ren'Py game instead of the RPG Maker one.</param>
    public static async Task<InstallBed> CreateAsync(Action<InstallBed>? packs = null, Action<IServiceCollection>? configure = null, bool renPy = false)
    {
        var bed = new InstallBed();
        bed._tool = renPy ? new RenPyRecipe() : new RpgMakerRecipe();
        string? gameDir = null;
        bed.Services = TestServices.Build(
            configure,
            steam =>
            {
                steam.AddGame(10, "Alpha").AddGame(20, "Beta");
                gameDir = Path.Combine(steam.Root, "steamapps", "common", "Alpha");
                if (renPy)
                {
                    RenPyFixture.Create(gameDir);
                }
                else
                {
                    RpgMakerFixture.Create(gameDir);
                }
            },
            registry: bed._registry.Path);
        bed.GameDir = gameDir!;
        packs?.Invoke(bed);
        bed._registry.Write();
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        await bed.Services.GetRequiredService<PackIndex>().RefreshAsync();
        return bed;
    }

    /// <summary>The catalog's copy of game 10.</summary>
    public Game Game => Services.GetRequiredService<GameLibrary>().Catalog.Find(Alpha)!;

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(GameDir, relativePath));

    /// <summary>
    /// Adds a recipe pack of game 10 to the index and writes its files: every string translated as "RU " + the original,
    /// made for <paramref name="buildId"/> with the current hashes of the touched files.
    /// </summary>
    public InstallBed RecipePack(
        string id,
        PackStatus status = PackStatus.Verified,
        string buildId = "1000",
        string language = "ru",
        int likes = 0,
        IReadOnlyList<PackDependency>? dependencies = null,
        bool writeStrings = true)
    {
        var recipe = _tool;
        var strings = recipe.Extract(GameDir).Strings.ToDictionary(s => s.Key, s => "RU " + s.Original, StringComparer.Ordinal);
        using var table = new MemoryStream();
        StringTableSerializer.Write(new StringTable(language, strings, new Dictionary<string, string>()), table);
        var tableBytes = table.ToArray();
        var files = recipe.TouchedFiles(GameDir, strings.Keys)
            .ToDictionary(f => f, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(GameDir, f)))), StringComparer.Ordinal);
        var date = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var manifest = new PackManifest(
            id,
            Alpha,
            language,
            id,
            [new PackAuthor("alice")],
            [new PackRevision(1, date, "alice")],
            status,
            new PackAgent("claude-code"),
            [new PackBuild(buildId, files)],
            new PackRecipe(recipe.Id, recipe.Version.ToString(3)),
            new PackStringsFile(PackStringsFile.DefaultPath, Convert.ToHexStringLower(SHA256.HashData(tableBytes)), tableBytes.Length),
            null,
            dependencies ?? [],
            [],
            new PackCoverage(strings.Count, strings.Count),
            date,
            date);
        var folder = $"games/steam-10/{language}/{id}";
        _registry.Pack(10, language, id, status, likes, buildId);
        _registry.WriteFile($"{folder}/manifest.json", PackManifestSerializer.Write(manifest));
        if (writeStrings)
        {
            var path = Path.Combine(_registry.Path, folder, "strings", "strings.json.gz");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, tableBytes);
        }

        return this;
    }

    public void Dispose()
    {
        Services?.Dispose();
        _registry.Dispose();
    }
}
