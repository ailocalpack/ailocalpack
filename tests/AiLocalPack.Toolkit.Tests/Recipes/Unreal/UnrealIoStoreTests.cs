using System.Buffers.Binary;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Unreal;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace AiLocalPack.Toolkit.Tests.Recipes.Unreal;

/// <summary><c>unreal</c> recipe on IoStore (<c>.utoc</c> / <c>.ucas</c>) and mixed pak + IoStore layouts (task 37).</summary>
public sealed class UnrealIoStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly UnrealRecipe _recipe = new();

    private string Game => _dir.Combine("game");

    private string Toc => _dir.Combine("game/" + UnrealFixture.Paks + "/pakchunk0.utoc");

    private string Output => _dir.Combine("out");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    public static TheoryData<byte, bool, string> Layouts => new()
    {
        { 3, false, "None" },
        { 4, false, "Zlib" },
        { 5, true, "Gzip" },
        { 6, true, "None" },
        { 7, false, "None" },
        { 8, true, "Zlib" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Extract_reads_localization_without_global_or_cooked_package_data(byte version, bool partitioned, string compression)
    {
        // Arrange
        Create(version, partitioned, compression);

        // Act
        var result = _recipe.Extract(Game, Ct);

        // Assert
        result.Strings.Select(s => s.Original).Should().BeEquivalentTo(UnrealFixture.Resource(3).Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 4)]
    public void Extract_names_the_index_and_every_data_partition(bool partitioned, int count)
    {
        // Arrange
        Create(partitioned: partitioned);

        // Act
        var result = _recipe.Extract(Game, Ct);

        // Assert
        result.Files.Should().HaveCount(count).And.Contain([UnrealFixture.Paks + "/pakchunk0.utoc", UnrealFixture.Paks + "/pakchunk0.ucas"]);
    }

    [Fact]
    public void Touched_files_are_the_containers_holding_the_keys()
    {
        // Arrange
        Create(partitioned: true);
        var result = _recipe.Extract(Game, Ct);

        // Act
        var touched = _recipe.TouchedFiles(Game, result.Strings.Select(s => s.Key));

        // Assert
        touched.Should().Equal(result.Files);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void The_fixture_is_read_by_CUE4Parse(byte version, bool partitioned, string compression)
    {
        // Arrange
        Create(version, partitioned, compression);
        using var independent = new IoStoreReader(Toc, versions: new VersionContainer(EGame.GAME_UE5_0));
        independent.Mount(StringComparer.Ordinal);

        // Act
        var english = independent.Files[UnrealFixture.English].Read();

        // Assert
        english.Should().Equal(UnrealFixture.Resource(3).Write());
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void The_fixture_chunk_metadata_is_read_by_CUE4Parse(byte version, bool partitioned, string compression)
    {
        // Arrange
        Create(version, partitioned, compression);
        using var archive = new FByteArchive(Toc, File.ReadAllBytes(Toc), new VersionContainer(EGame.GAME_UE5_0));

        // Act
        var toc = new FIoStoreTocResource(archive, EIoStoreTocReadOptions.ReadTocMeta);

        // Assert
        toc.ChunkMetas!.Select(m => m.Flags).Should().Equal(Enumerable.Repeat(compression == "None" ? FIoStoreTocEntryMetaFlags.None : FIoStoreTocEntryMetaFlags.Compressed, 3));
    }

    [Fact]
    public void Rebuild_refuses_localization_kept_inside_IoStore()
    {
        // Arrange
        Create();
        var values = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => "RU " + s.Original);

        // Act
        var act = () => _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*inside IoStore containers*can't write a mod*");
    }

    [Fact]
    public void Mixed_layout_with_localization_in_the_pak_names_only_the_pak()
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { ["Sample/Content/Opaque.uasset"] = [1, 2, 3] });

        // Act
        var result = _recipe.Extract(Game, Ct);

        // Assert
        result.Files.Should().Equal(UnrealFixture.Pak);
    }

    [Fact]
    public void Mixed_layout_with_localization_in_the_pak_rebuilds_a_mod_pak()
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { ["Sample/Content/Opaque.uasset"] = [1, 2, 3] });
        var values = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => "RU " + s.Original);

        // Act
        var result = _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        result.ChangedFiles.Should().Equal(Mods(UnrealRecipe.ModName), Mods(UnrealRecipe.ModData), Mods(UnrealRecipe.ModToc));
    }

    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)8)]
    public void The_mod_toc_is_an_empty_container_of_the_game_toc_version(byte version)
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { ["Sample/Content/Opaque.uasset"] = [1, 2, 3] }, version);
        var values = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => "RU " + s.Original);
        _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);
        var path = Path.Combine(Output, Mods(UnrealRecipe.ModToc));
        using var archive = new FByteArchive(path, File.ReadAllBytes(path), new VersionContainer(EGame.GAME_UE5_0));

        // Act
        var header = new FIoStoreTocHeader(archive);

        // Assert
        ((byte)header.Version, header.TocEntryCount, header.DirectoryIndexSize, archive.Length).Should().Be((version, 0u, 0u, 144L));
    }

    [Fact]
    public void The_mod_data_container_is_empty()
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { ["Sample/Content/Opaque.uasset"] = [1, 2, 3] });
        var values = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => "RU " + s.Original);

        // Act
        _recipe.Rebuild(Game, "ru", values, new DirectoryRebuildOutput(Output), Ct);

        // Assert
        new FileInfo(Path.Combine(Output, Mods(UnrealRecipe.ModData))).Length.Should().Be(0);
    }

    [Fact]
    public void Extract_ignores_an_installed_mod_with_its_IoStore_pair()
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { ["Sample/Content/Opaque.uasset"] = [1, 2, 3] });
        var before = _recipe.Extract(Game, Ct);
        _recipe.Rebuild(Game, "ru", before.Strings.ToDictionary(s => s.Key, s => "RU " + s.Original), new DirectoryRebuildOutput(Output), Ct);
        _dir.CopyFolder(Output, "game");

        // Act
        var after = _recipe.Extract(Game, Ct);

        // Assert
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Mixed_layout_with_localization_in_IoStore_names_its_containers()
    {
        // Arrange
        UnrealFixture.Create(Game);
        File.WriteAllBytes(Path.Combine(Game, UnrealFixture.Pak), UnrealPakWriter.Write(new Dictionary<string, byte[]> { ["Sample/Content/Other.bin"] = [1] }));
        Create();

        // Act
        var result = _recipe.Extract(Game, Ct);

        // Assert
        result.Files.Should().Equal(UnrealFixture.Paks + "/pakchunk0.ucas", UnrealFixture.Paks + "/pakchunk0.utoc");
    }

    [Fact]
    public void The_same_path_in_a_pak_and_in_IoStore_is_refused()
    {
        // Arrange
        UnrealFixture.Create(Game);
        Create();

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Ambiguous duplicate path*");
    }

    [Fact]
    public void Paths_differing_only_in_case_are_also_refused()
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { [UnrealFixture.English.ToUpperInvariant()] = UnrealFixture.Resource(3).Write() });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Ambiguous duplicate path*");
    }

    [Fact]
    public void Unrelated_files_at_the_container_root_are_fine()
    {
        // Arrange
        UnrealFixture.Create(Game);
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { ["Build.txt"] = [1, 2, 3] });

        // Act
        var result = _recipe.Extract(Game, Ct);

        // Assert
        result.Strings.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("pakchunk0.ucas")]
    [InlineData("pakchunk0_s1.ucas")]
    public void A_missing_data_partition_is_named(string missing)
    {
        // Arrange
        Create(partitioned: true);
        File.Delete(Path.Combine(Path.GetDirectoryName(Toc)!, missing));

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Missing IoStore data partition*").Which.File.Should().EndWith(missing);
    }

    [Fact]
    public void Data_without_its_index_is_refused()
    {
        // Arrange
        UnrealFixture.Create(Game);
        Create();
        File.Delete(Toc);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*no matching .utoc*");
    }

    [Fact]
    public void Global_metadata_is_not_read()
    {
        // Arrange
        Create();
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Toc)!, "global.utoc"), [1, 2, 3]);

        // Act
        var result = _recipe.Extract(Game, Ct);

        // Assert
        result.Strings.Should().HaveCount(3);
    }

    [Fact]
    public void Cooked_asset_chunks_are_not_taken_for_raw_locres()
    {
        // Arrange
        Create();

        // The metadata's chunk becomes ExportBundleData instead of ExternalFile.
        Change(bytes => bytes[144 + 11] = 1);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*cooked asset FText is unsupported*");
    }

    [Theory]
    [InlineData(16, 9, "*version 9*")]
    [InlineData(80, 10, "*AES*no known key*")]
    [InlineData(80, 12, "*Signed*unsupported*")]
    [InlineData(80, 0, "*directory index*")]
    public void Unsupported_versions_flags_and_encryption_are_refused(int offset, byte value, string message)
    {
        // Arrange
        Create();
        Change(bytes => bytes[offset] = value);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage(message);
    }

    [Theory]
    [InlineData("Oodle")]
    [InlineData("UnknownCodec")]
    public void Compression_without_our_own_decoder_is_refused(string codec)
    {
        // Arrange
        Create(compression: codec);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Unavailable IoStore localization compression*no game DLL*");
    }

    [Theory]
    [InlineData(24, uint.MaxValue)]
    [InlineData(36, uint.MaxValue)]
    [InlineData(44, 0U)]
    [InlineData(44, 3U)]
    [InlineData(52, uint.MaxValue)]
    public void Oversized_counts_and_bad_block_sizes_are_refused(int offset, uint value)
    {
        // Arrange
        Create();
        Change(bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value));

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*oversized IoStore TOC header*");
    }

    [Fact]
    public void A_directory_cycle_is_refused_without_hanging()
    {
        // Arrange
        Create();
        Change(bytes =>
        {
            // Header + three chunk IDs, extents and blocks, then the mount point FString and the directory count.
            var directory = 144 + (3 * 22) + (3 * 12);
            var mountSize = 4 + "../../../".Length + 1;

            // The root's first child = the root.
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + mountSize + 4 + 4), 0);
        });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*cyclic IoStore directory index*");
    }

    [Theory]
    [InlineData("../Game.locres")]
    [InlineData("Sample/../Game.locres")]
    [InlineData("Sample/C:/Game.locres")]
    [InlineData("Sample/Bad\\Name/Game.locres")]
    public void Unsafe_path_components_are_refused(string path)
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(Game, "Engine"));
        IoStoreFixture.Write(Toc, new Dictionary<string, byte[]> { [path] = UnrealFixture.Resource(3).Write() });

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Invalid IoStore directory name*");
    }

    [Fact]
    public void Every_truncation_before_the_end_of_the_directory_is_refused()
    {
        // Arrange
        Create();
        var bytes = File.ReadAllBytes(Toc);
        var directoryEnd = 144 + (3 * 22) + (3 * 12) + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48));

        // Act
        var accepted = Enumerable.Range(0, directoryEnd).Where(length =>
        {
            File.WriteAllBytes(Toc, bytes[..length]);
            try
            {
                _recipe.Extract(Game, Ct);
                return true;
            }
            catch (RecipeException)
            {
                return false;
            }
        }).ToList();

        // Assert
        accepted.Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)8)]
    public void Truncated_chunk_metadata_after_the_directory_is_refused(byte version)
    {
        // Arrange
        Create(version);
        File.WriteAllBytes(Toc, File.ReadAllBytes(Toc)[..^1]);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Truncated IoStore TOC*");
    }

    [Fact]
    public void A_truncated_data_partition_is_refused()
    {
        // Arrange
        Create();
        File.WriteAllBytes(Path.ChangeExtension(Toc, ".ucas"), [1]);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*truncated data partition*");
    }

    [Fact]
    public void Damaged_data_names_the_resource_it_holds()
    {
        // Arrange
        Create();
        var data = File.ReadAllBytes(Path.ChangeExtension(Toc, ".ucas"));
        data[0] ^= 0xFF;
        File.WriteAllBytes(Path.ChangeExtension(Toc, ".ucas"), data);

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be(UnrealFixture.Meta);
    }

    [Fact]
    public void Extract_stops_when_cancelled()
    {
        // Arrange
        Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var act = () => _recipe.Extract(Game, cancelled.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void An_opened_partition_is_closed_when_a_later_one_fails_to_open()
    {
        // Arrange
        Create(partitioned: true);
        var opened = new List<FileStream>();

        // Act
        var act = () => new UnrealIoStore(Toc, Ct, partition =>
        {
            if (opened.Count > 0)
            {
                throw new IOException("Synthetic second partition failure.");
            }

            opened.Add(File.OpenRead(partition));
            return new FStreamArchive(partition, opened[0], new VersionContainer(EGame.GAME_UE5_0));
        });

        // Assert
        act.Should().Throw<Exception>().WithMessage("*Failed to open container partition 1*");
        opened.Should().ContainSingle().Which.CanRead.Should().BeFalse();
    }

    [Fact]
    public void An_opened_partition_is_closed_when_cancelled_midway()
    {
        // Arrange
        Create(partitioned: true);
        using var cts = new CancellationTokenSource();
        var opened = new List<FileStream>();

        // Act
        var act = () => new UnrealIoStore(Toc, cts.Token, partition =>
        {
            opened.Add(File.OpenRead(partition));
            cts.Cancel();
            return new FStreamArchive(partition, opened[^1], new VersionContainer(EGame.GAME_UE5_0));
        });

        // Assert
        act.Should().Throw<OperationCanceledException>();
        opened.Should().OnlyContain(s => !s.CanRead);
    }

    [Fact]
    public void Partitions_are_closed_when_the_reader_fails_after_opening_them()
    {
        // Arrange
        Create(version: 5, partitioned: true);
        var bytes = File.ReadAllBytes(Toc);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), 1); // one perfect-hash seed
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), 1); // one overflow chunk index
        var insert = 144 + (3 * 22);
        byte[] corruptHashTable = [1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        File.WriteAllBytes(Toc, [.. bytes[..insert], .. corruptHashTable, .. bytes[insert..]]);
        var opened = new List<FileStream>();

        // Act
        var act = () => new UnrealIoStore(Toc, Ct, partition =>
        {
            opened.Add(File.OpenRead(partition));
            return new FStreamArchive(partition, opened[^1], new VersionContainer(EGame.GAME_UE5_0));
        });

        // Assert
        act.Should().Throw<IndexOutOfRangeException>();
        opened.Should().HaveCount(3).And.OnlyContain(s => !s.CanRead);
    }

    private static string Mods(string file) => UnrealFixture.Paks + "/~mods/" + file;

    private void Change(Action<byte[]> change)
    {
        var bytes = File.ReadAllBytes(Toc);
        change(bytes);
        File.WriteAllBytes(Toc, bytes);
    }

    private void Create(byte version = 3, bool partitioned = false, string compression = "None")
    {
        Directory.CreateDirectory(Path.Combine(Game, "Engine"));
        IoStoreFixture.Write(
            Toc,
            new Dictionary<string, byte[]>
            {
                [UnrealFixture.English] = UnrealFixture.Resource(3).Write(),
                [UnrealFixture.French] = UnrealFixture.Resource(3, true).Write(),
                [UnrealFixture.Meta] = UnrealFixture.Metadata(2),
            },
            version,
            partitioned,
            compression);
    }
}
