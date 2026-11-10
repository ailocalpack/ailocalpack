using System.IO.Compression;
using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Patches;

public sealed class PatchArchiveWriterTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Original => Path.Combine(_temp.Path, "original");

    private string Patched => Path.Combine(_temp.Path, "patched");

    private string ArchivePath => Path.Combine(_temp.Path, "out", "patch.zip");

    private void Write(string root, string file, byte[] content)
    {
        var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void Arrange()
    {
        Write(Original, "data/strings.bin", "HELLO\0WORLD\0"u8.ToArray());
        Write(Patched, "data/strings.bin", "ПРИВЕТ\0МИР\0"u8.ToArray());
        Write(Original, "menu.dat", "Start Quit"u8.ToArray());
        Write(Patched, "menu.dat", "Начать Выйти"u8.ToArray());
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void The_archive_holds_one_vcdiff_entry_per_file_in_ordinal_order()
    {
        // Arrange
        Arrange();

        // Act
        PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["menu.dat", "data/strings.bin"], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        using var zip = ZipFile.OpenRead(ArchivePath);
        zip.Entries.Select(e => e.FullName).Should().Equal("data/strings.bin.vcdiff", "menu.dat.vcdiff");
    }

    [Fact]
    public void The_result_names_the_hashes_of_the_originals_the_patched_files_and_the_archive()
    {
        // Arrange
        Arrange();

        // Act
        var archive = PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["menu.dat"], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        archive.Should().BeEquivalentTo(new PatchArchive(
            Sha(File.ReadAllBytes(ArchivePath)),
            new FileInfo(ArchivePath).Length,
            new Dictionary<string, string> { ["menu.dat"] = Sha("Start Quit"u8.ToArray()) },
            new Dictionary<string, string> { ["menu.dat"] = Sha("Начать Выйти"u8.ToArray()) }));
    }

    [Fact]
    public void The_same_files_give_the_same_archive()
    {
        // Arrange
        Arrange();
        var first = PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["menu.dat", "data/strings.bin"], cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var second = PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["data/strings.bin", "menu.dat"], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        second.Sha256.Should().Be(first.Sha256);
    }

    [Fact]
    public void The_patch_applier_installs_the_archive_over_the_originals()
    {
        // Arrange
        Arrange();
        var archive = PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["menu.dat", "data/strings.bin"], cancellationToken: TestContext.Current.CancellationToken);
        var manifest = PatchManifest(archive);
        var output = Path.Combine(_temp.Path, "applied");

        // Act
        var result = new PatchApplier(new FileHashCache()).Apply(manifest, ArchivePath, Original, output, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Failure, File.ReadAllText(Path.Combine(output, "menu.dat"))).Should().Be((null, "Начать Выйти"));
    }

    [Fact]
    public void A_path_outside_the_folders_is_refused()
    {
        // Arrange
        Arrange();

        // Act
        var act = () => PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["../menu.dat"]);

        // Assert
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void A_missing_patched_file_fails_without_leaving_an_archive()
    {
        // Arrange
        Arrange();

        // Act
        var act = () => PatchArchiveWriter.Write(ArchivePath, Original, Patched, ["missing.dat"]);

        // Assert
        act.Should().Throw<IOException>();
        Directory.GetFiles(Path.GetDirectoryName(ArchivePath)!).Should().BeEmpty();
    }

    private static PackManifest PatchManifest(PatchArchive archive)
    {
        var date = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        return new PackManifest(
            "test",
            GameId.Parse("fp:" + new string('a', 64)),
            "ru",
            "Test",
            [new PackAuthor("local-author")],
            [new PackRevision(1, date, "local-author")],
            PackStatus.Alpha,
            null,
            [new PackBuild(null, archive.Originals)],
            null,
            null,
            new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/patch.zip"), archive.Sha256, archive.Size, archive.Patched),
            [],
            [],
            new PackCoverage(1, 1),
            date,
            date);
    }
}
