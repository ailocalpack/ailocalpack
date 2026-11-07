using System.IO.Compression;
using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Patches;

public sealed class PatchApplierTests : IDisposable
{
    private const string BinFile = "data/level.bin";
    private const string TextFile = "data/text.txt";

    private readonly TempDir _dir = new();
    private readonly PatchApplier _applier = new(new FileHashCache());

    public PatchApplierTests()
    {
        _dir.File("game/" + BinFile, File.ReadAllBytes(VcdiffDecoderTests.Fixture("source.bin")));
        _dir.File("game/" + TextFile, "Hello, lamplighter");
    }

    public void Dispose() => _dir.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Target => File.ReadAllBytes(VcdiffDecoderTests.Fixture("target.bin"));

    private static readonly byte[] TextTarget = "Привет, фонарщик"u8.ToArray();

    private string GameDir => _dir.Combine("game");

    private string Output => _dir.Combine("out");

    private string Archive => _dir.Combine("patch.zip");

    private static PackManifest Manifest(IReadOnlyDictionary<string, string> originals, IReadOnlyDictionary<string, string> patched)
    {
        var date = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        return new PackManifest(
            "patch-ru",
            GameId.Steam(10),
            "ru",
            "Patch",
            [new PackAuthor("alice")],
            [new PackRevision(1, date, "alice")],
            PackStatus.Alpha,
            null,
            [new PackBuild("1000", originals)],
            null,
            null,
            new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/patch.zip"), new string('a', 64), 1, patched),
            [],
            [],
            new PackCoverage(1, 1),
            date,
            date);
    }

    /// <summary>A manifest for both files and the archive holding xdelta3's delta for the binary and a synthetic one for the text.</summary>
    private PackManifest Pack()
    {
        var textSource = File.ReadAllBytes(Path.Combine(GameDir, TextFile));
        VcdiffFixture.Archive(Archive, new Dictionary<string, byte[]>
        {
            [BinFile] = File.ReadAllBytes(VcdiffDecoderTests.Fixture("lzma.vcdiff")),
            [TextFile] = VcdiffFixture.Encode(textSource, TextTarget),
        });
        return Manifest(
            new Dictionary<string, string>
            {
                [BinFile] = Sha(File.ReadAllBytes(Path.Combine(GameDir, BinFile))),
                [TextFile] = Sha(textSource),
            },
            new Dictionary<string, string> { [BinFile] = Sha(Target), [TextFile] = Sha(TextTarget) });
    }

    private PatchResult Apply(PackManifest manifest) => _applier.Apply(manifest, Archive, GameDir, Output, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void The_patched_files_are_written_into_the_output_folder()
    {
        // Arrange
        var manifest = Pack();

        // Act
        var result = Apply(manifest);

        // Assert
        result.Files.Should().Equal(BinFile, TextFile);
        File.ReadAllBytes(Path.Combine(Output, BinFile)).Should().Equal(Target);
        File.ReadAllBytes(Path.Combine(Output, TextFile)).Should().Equal(TextTarget);
    }

    [Fact]
    public void The_game_folder_is_not_written()
    {
        // Arrange
        var manifest = Pack();

        // Act
        Apply(manifest);

        // Assert
        File.ReadAllText(Path.Combine(GameDir, TextFile)).Should().Be("Hello, lamplighter");
    }

    [Fact]
    public void A_game_file_other_than_the_one_the_patch_was_made_from_is_a_source_mismatch()
    {
        // Arrange
        var manifest = Pack();
        _dir.File("game/" + TextFile, "Hello, modded lamplighter");

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure.Should().BeEquivalentTo(new
        {
            Problem = PatchProblem.SourceMismatch,
            File = TextFile,
            ExpectedSha256 = manifest.Builds[0].Files[TextFile],
            ActualSha256 = Sha("Hello, modded lamplighter"u8.ToArray()),
        });
    }

    [Fact]
    public void A_missing_game_file_is_reported()
    {
        // Arrange
        var manifest = Pack();
        File.Delete(Path.Combine(GameDir, BinFile));

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure.Should().BeEquivalentTo(new { Problem = PatchProblem.SourceMissing, File = BinFile });
    }

    [Fact]
    public void A_file_already_patched_is_copied_as_it_is()
    {
        // Arrange
        var manifest = Pack();
        _dir.File("game/" + TextFile, TextTarget);

        // Act
        var result = Apply(manifest);

        // Assert
        result.AlreadyPatched.Should().Equal(TextFile);
        File.ReadAllBytes(Path.Combine(Output, TextFile)).Should().Equal(TextTarget);
    }

    [Fact]
    public void A_result_without_the_manifest_hash_is_a_result_mismatch()
    {
        // Arrange
        var manifest = Pack();
        manifest = manifest with { Patch = manifest.Patch! with { Files = new Dictionary<string, string>(manifest.Patch.Files) { [TextFile] = new string('0', 64) } } };

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure.Should().BeEquivalentTo(new { Problem = PatchProblem.ResultMismatch, File = TextFile, ActualSha256 = Sha(TextTarget) });
    }

    [Fact]
    public void A_delta_the_decoder_refuses_is_a_decode_failure_with_its_problem()
    {
        // Arrange
        var manifest = Pack();
        File.Delete(Archive);
        VcdiffFixture.Archive(Archive, new Dictionary<string, byte[]>
        {
            [BinFile] = File.ReadAllBytes(VcdiffDecoderTests.Fixture("djw.vcdiff")),
            [TextFile] = [1, 2, 3],
        });

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure.Should().BeEquivalentTo(new { Problem = PatchProblem.Decode, File = BinFile, Vcdiff = VcdiffProblem.Unsupported });
    }

    [Fact]
    public void An_archive_entry_the_manifest_does_not_name_is_refused()
    {
        // Arrange
        var manifest = Pack();
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Update))
        {
            zip.CreateEntry("../outside.vcdiff");
        }

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure!.Problem.Should().Be(PatchProblem.ArchiveInvalid);
        File.Exists(_dir.Combine("outside")).Should().BeFalse();
    }

    [Fact]
    public void Folder_entries_as_zip_tools_write_them_are_ignored()
    {
        // Arrange
        var manifest = Pack();
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Update))
        {
            zip.CreateEntry("data/");
        }

        // Act
        var result = Apply(manifest);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void An_archive_missing_a_touched_file_is_refused()
    {
        // Arrange
        var manifest = Pack();
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Update))
        {
            zip.GetEntry(TextFile + ".vcdiff")!.Delete();
        }

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure.Should().BeEquivalentTo(new { Problem = PatchProblem.ArchiveInvalid, File = TextFile });
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        // Arrange
        var manifest = Pack();
        File.WriteAllText(Archive, "not a zip");

        // Act
        var result = Apply(manifest);

        // Assert
        result.Failure!.Problem.Should().Be(PatchProblem.ArchiveInvalid);
    }

    [Fact]
    public void Progress_counts_the_files()
    {
        // Arrange
        var manifest = Pack();
        var progress = new RecordingProgress<PatchProgress>();

        // Act
        _applier.Apply(manifest, Archive, GameDir, Output, progress, TestContext.Current.CancellationToken);

        // Assert
        progress.Reports.Should().Equal(new PatchProgress(0, 2), new PatchProgress(1, 2), new PatchProgress(2, 2));
    }
}
