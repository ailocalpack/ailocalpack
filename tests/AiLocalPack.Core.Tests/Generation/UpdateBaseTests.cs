using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>An update job's base pack (task 56): read from the installed pack, kept in the job folder.</summary>
public sealed class UpdateBaseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameId Game = GameId.Steam(620);

    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly LocalPackStore _local;
    private readonly PackCache _cache;

    public UpdateBaseTests()
    {
        _paths = AppPaths.UnderHome(_dir.Combine("home"));
        _local = new LocalPackStore(_paths);
        _cache = new PackCache(_paths);
    }

    public void Dispose() => _dir.Dispose();

    private static PackManifest Manifest(int revision = 3, bool patch = false) => new(
        "lanterns",
        Game,
        "ru",
        "Фонарщики",
        [new PackAuthor("alice")],
        [.. Enumerable.Range(1, revision).Select(n => new PackRevision(n, T0.AddDays(n - 10), "alice"))],
        PackStatus.Verified,
        null,
        [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) })],
        patch ? null : new PackRecipe("rpgmaker", "1.0.0"),
        null,
        patch ? new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.org/p.zip"), new string('c', 64), 10, new Dictionary<string, string> { ["data/Items.json"] = new('d', 64) }) : null,
        [],
        [],
        new PackCoverage(1, 2),
        T0.AddDays(-9),
        T0);

    private static StringTable Table { get; } = new StringTable("ru", new Dictionary<string, string> { ["a"] = "Зелье" })
    {
        Originals = new Dictionary<string, string> { ["a"] = OriginalHash.Of("Potion") },
    };

    private static Glossary Glossary { get; } = new("ru", "informal", [new GlossaryEntry("Potion", "Зелье")]);

    private static InstalledPack Record(bool local, int revision = 3) =>
        new(Game, "ru", "lanterns", revision, T0, "/games/sample", "backup", [], []) { Local = local };

    private static LocalPackReport Report => new("job-1", T0, TestRunResult.Skipped(TestRunSkip.TurnedOff, "off"), LocalPackFonts.NotChecked, [], [], 0);

    /// <summary>A registry pack's cache entry as the fetcher leaves it: the string table, then the manifest.</summary>
    private void Cache(PackManifest manifest)
    {
        using var compressed = new MemoryStream();
        StringTableSerializer.Write(Table, compressed);
        var strings = compressed.ToArray();
        var named = manifest with { Strings = new PackStringsFile(PackStringsFile.DefaultPath, PackCache.Sha256Hex(strings), strings.LongLength) };
        var text = PackManifestSerializer.Write(named);
        var folder = _cache.EntryFolder(named, PackCache.Sha256Hex(Encoding.UTF8.GetBytes(text)));
        Directory.CreateDirectory(Path.Combine(folder, "strings"));
        File.WriteAllBytes(Path.Combine(folder, "strings", "strings.json.gz"), strings);
        File.WriteAllText(Path.Combine(folder, PackCache.ManifestFileName), text);
    }

    [Fact]
    public void A_local_base_comes_with_its_strings_and_glossary()
    {
        // Arrange
        _local.Save(Manifest(), Table, Report, Glossary);

        // Act
        var load = UpdateBaseLoader.Load(Record(local: true), _local, _cache);

        // Assert
        new { load.Local, load.Base!.Strings.Originals, Terms = load.Base.Glossary!.Entries.Count, load.Reference }.Should().BeEquivalentTo(new
        {
            Local = true,
            Table.Originals,
            Terms = 1,
            Reference = new GenerationBase("lanterns", 3, true, "Фонарщики"),
        });
    }

    [Fact]
    public void A_local_pack_at_another_revision_is_missing()
    {
        // Arrange
        _local.Save(Manifest(revision: 4), Table, Report);

        // Act
        var load = UpdateBaseLoader.Load(Record(local: true, revision: 3), _local, _cache);

        // Assert
        load.Problem.Should().Be(UpdateBaseProblem.Missing);
    }

    [Fact]
    public void A_registry_base_comes_from_the_pack_cache()
    {
        // Arrange
        Cache(Manifest());

        // Act
        var load = UpdateBaseLoader.Load(Record(local: false), _local, _cache);

        // Assert
        new { load.Local, Strings = load.Base!.Strings.Strings, load.Base.Glossary }.Should().BeEquivalentTo(new
        {
            Local = false,
            Table.Strings,
            Glossary = (Glossary?)null,
        });
    }

    [Fact]
    public void A_registry_pack_not_in_the_cache_is_missing()
    {
        // Act
        var load = UpdateBaseLoader.Load(Record(local: false), _local, _cache);

        // Assert
        load.Problem.Should().Be(UpdateBaseProblem.Missing);
    }

    [Fact]
    public void A_binary_patch_pack_has_no_strings_to_build_on()
    {
        // Act
        var load = UpdateBaseLoader.Read(Manifest(patch: true), _dir.Combine("pack"), null);

        // Assert
        load.Problem.Should().Be(UpdateBaseProblem.NoStrings);
    }

    [Fact]
    public void A_damaged_string_table_is_unreadable()
    {
        // Arrange
        _local.Save(Manifest(), Table, Report);
        var pack = _local.Find(Game, "ru", "lanterns")!;
        File.WriteAllText(Path.Combine(pack.Folder, "strings", "strings.json.gz"), "not a table");

        // Act
        var load = UpdateBaseLoader.Load(Record(local: true), _local, _cache);

        // Assert
        load.Problem.Should().Be(UpdateBaseProblem.Unreadable);
    }

    [Fact]
    public void The_base_files_read_back_from_the_job_folder()
    {
        // Arrange
        var files = new GenerationFiles(_dir.Combine("job"));
        var written = new UpdateBase(Manifest() with { Strings = new PackStringsFile(PackStringsFile.DefaultPath, new string('b', 64), 10) }, Table, Glossary);

        // Act
        files.WriteBase(written);

        // Assert
        var read = files.ReadBase()!;
        new { read.Manifest.Id, read.Manifest.Revision, read.Strings.Strings, read.Strings.Originals, Terms = read.Glossary!.Entries.Count }.Should().BeEquivalentTo(new
        {
            Id = "lanterns",
            Revision = 3,
            Table.Strings,
            Table.Originals,
            Terms = 1,
        });
    }

    [Fact]
    public void A_job_without_base_files_has_no_base()
    {
        // Arrange
        var files = new GenerationFiles(_dir.Combine("job"));

        // Act
        var read = files.ReadBase();

        // Assert
        read.Should().BeNull();
    }

    [Fact]
    public void A_local_pack_keeps_its_glossary_and_deletes_it_with_the_pack()
    {
        // Arrange
        _local.Save(Manifest(), Table, Report, Glossary);

        // Act
        _local.Delete(Game, "ru", "lanterns");

        // Assert
        File.Exists(_local.GlossaryPath(Game, "ru", "lanterns")).Should().BeFalse();
    }

    [Fact]
    public void A_local_packs_update_report_reads_back()
    {
        // Arrange
        var report = Report with { Update = new LocalPackUpdate(3, 12, 2, 1) };

        // Act
        _local.Save(Manifest(revision: 4), Table, report);

        // Assert
        _local.Find(Game, "ru", "lanterns")!.Report.Update.Should().Be(new LocalPackUpdate(3, 12, 2, 1));
    }
}
