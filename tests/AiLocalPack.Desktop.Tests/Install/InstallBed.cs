using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RenPy;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using AiLocalPack.Toolkit.Recipes.Unity;
using AiLocalPack.Toolkit.Recipes.Unreal;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>
/// A real install end to end: Steam game 10 "Alpha" (build 1000) holding the synthetic RPG Maker MV game, Steam game 20
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
    public const string SystemFile = "www/data/System.json";

    /// <param name="renPy">Game 10 is the synthetic Ren'Py game instead of the RPG Maker one.</param>
    /// <param name="unity">Game 10 is the synthetic Unity game instead of the RPG Maker one.</param>
    /// <param name="unreal">Game 10 is the synthetic Unreal game instead of the RPG Maker one.</param>
    public static async Task<InstallBed> CreateAsync(Action<InstallBed>? packs = null, Action<IServiceCollection>? configure = null, bool renPy = false, bool unity = false, bool unreal = false)
    {
        var bed = new InstallBed();
        bed._tool = unreal ? new UnrealRecipe() : unity ? new UnityRecipe() : renPy ? new RenPyRecipe() : new RpgMakerRecipe();
        string? gameDir = null;
        bed.Services = TestServices.Build(
            configure,
            steam =>
            {
                steam.AddGame(10, "Alpha").AddGame(20, "Beta");
                gameDir = Path.Combine(steam.Root, "steamapps", "common", "Alpha");
                if (unreal)
                {
                    UnrealFixture.Create(gameDir);
                }
                else if (unity)
                {
                    UnityFixture.Create(gameDir);
                }
                else if (renPy)
                {
                    RenPyFixture.Create(gameDir);
                }
                else
                {
                    RpgMakerFixture.Create(gameDir, mv: true);
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
    /// Adds a recipe pack of game 10 to the index and writes its files: every string translated as <paramref name="prefix"/>
    /// + the original ("RU ": letters every font has; "РУ " needs a font with Cyrillic), made for <paramref name="buildId"/>
    /// with the current hashes of the touched files.
    /// </summary>
    public InstallBed RecipePack(
        string id,
        PackStatus status = PackStatus.Verified,
        string buildId = "1000",
        string language = "ru",
        int likes = 0,
        IReadOnlyList<PackDependency>? dependencies = null,
        bool writeStrings = true,
        string prefix = "RU ",
        IReadOnlyList<string>? fonts = null)
    {
        var recipe = _tool;
        var strings = recipe.Extract(GameDir).Strings.ToDictionary(s => s.Key, s => prefix + s.Original, StringComparer.Ordinal);
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
            fonts ?? [],
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

    /// <summary>
    /// Saves a pack of game 10 generated on this machine (task 55) into the local packs, every string translated as
    /// <paramref name="prefix"/> + the original, made by a completed generation job (its folder in the work folder) unless
    /// <paramref name="withJob"/> is off. The generation service reads the jobs again, so the pack is the game's result.
    /// </summary>
    public LocalPack LocalPack(
        string id = "claude-code",
        string language = "ru",
        string prefix = "RU ",
        bool withJob = true,
        TestRunResult? testRun = null,
        IReadOnlyList<LocalPackWarning>? warnings = null,
        IReadOnlyList<LocalPackString>? untranslated = null,
        DateTimeOffset? tried = null)
    {
        var jobId = "job-" + id;
        if (withJob)
        {
            var (entry, jobLock) = Services.GetRequiredService<GenerationJobStore>().Create(Game, language, "claude", GenerationOptions.Default);
            jobLock.Dispose();
            jobId = entry.Job.Id;
            GenerationJobStore.Write(entry.Folder, entry.Job with { Stage = GenerationStage.Done, Status = GenerationJobStatus.Completed });
        }

        var recipe = _tool;
        var strings = recipe.Extract(GameDir).Strings.ToDictionary(s => s.Key, s => prefix + s.Original, StringComparer.Ordinal);
        var files = recipe.TouchedFiles(GameDir, strings.Keys)
            .ToDictionary(f => f, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(GameDir, f)))), StringComparer.Ordinal);
        var date = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var total = strings.Count + (untranslated?.Count ?? 0);
        var manifest = new PackManifest(
            id,
            Alpha,
            language,
            "Claude Code translation",
            [new PackAuthor(LocalPackStore.PlaceholderAuthor)],
            [new PackRevision(1, date, LocalPackStore.PlaceholderAuthor)],
            PackStatus.Alpha,
            new PackAgent("claude-code", "2.1.319", "claude-opus-5-5"),
            [new PackBuild("1000", files)],
            new PackRecipe(recipe.Id, recipe.Version.ToString(3)),
            null,
            null,
            [],
            ["noto-sans"],
            new PackCoverage(strings.Count, total),
            date,
            date);
        var report = new LocalPackReport(
            jobId,
            date,
            testRun ?? TestRunResult.Passed(TimeSpan.FromSeconds(15)),
            new LocalPackFonts(true, "noto-sans", ["Fixture Sans"], []),
            warnings ?? [],
            untranslated ?? [],
            0,
            Tried: tried);
        var pack = Services.GetRequiredService<LocalPackStore>().Save(manifest, new StringTable(language, strings), report);
        Services.GetRequiredService<GenerationService>().Recover();
        return pack;
    }

    /// <summary>The text <see cref="PatchPack"/> puts into <see cref="SystemFile"/>.</summary>
    public const string PatchedTitle = "Патч: Фонарщики";

    /// <summary>
    /// Adds a binary patch pack of game 10 (task 34): a VCDIFF delta turning the current <see cref="SystemFile"/> into one
    /// whose game title is <see cref="PatchedTitle"/>, in the registry's <c>releases/</c> folder (standing in for the
    /// Release asset), and a manifest with the original and patched hashes.
    /// </summary>
    /// <param name="delta">Archived instead of the real delta (a damaged patch whose archive still has the manifest's hash).</param>
    public InstallBed PatchPack(string id, string buildId = "1000", string language = "ru", byte[]? delta = null)
    {
        var path = Path.Combine(GameDir, SystemFile);
        var original = File.ReadAllBytes(path);
        var patched = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\"gameTitle\":", $"\"gameTitle\":\"{PatchedTitle}\",\"oldTitle\":", StringComparison.Ordinal));
        var asset = $"steam-10-{language}-{id}-1.zip";
        var archive = Path.Combine(_registry.Path, "releases", asset);
        VcdiffFixture.Archive(archive, new Dictionary<string, byte[]> { [SystemFile] = delta ?? VcdiffFixture.Encode(original, patched) });
        var archiveBytes = File.ReadAllBytes(archive);
        string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        var date = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var manifest = new PackManifest(
            id,
            Alpha,
            language,
            id,
            [new PackAuthor("alice")],
            [new PackRevision(1, date, "alice")],
            PackStatus.Verified,
            new PackAgent("claude-code"),
            [new PackBuild(buildId, new Dictionary<string, string> { [SystemFile] = Sha(original) })],
            null,
            null,
            new PackPatch(
                PackPatch.VcdiffFormat,
                new Uri($"https://github.com/ailocalpack/registry/releases/download/packs/{asset}"),
                Sha(archiveBytes),
                archiveBytes.Length,
                new Dictionary<string, string> { [SystemFile] = Sha(patched) }),
            [],
            [],
            new PackCoverage(1, 1),
            date,
            date);
        _registry.Pack(10, language, id, PackStatus.Verified, 0, buildId);
        _registry.WriteFile($"games/steam-10/{language}/{id}/manifest.json", PackManifestSerializer.Write(manifest));
        return this;
    }

    public void Dispose()
    {
        Services?.Dispose();
        _registry.Dispose();
    }
}
