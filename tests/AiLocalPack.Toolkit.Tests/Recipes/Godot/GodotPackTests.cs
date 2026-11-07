using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Toolkit.Recipes.Godot;

namespace AiLocalPack.Toolkit.Tests.Recipes.Godot;

/// <summary><see cref="GodotPack"/>: Godot 3 / 4 pack headers, embedded packs, refusals, rewriting (task 35).</summary>
public sealed class GodotPackTests : IDisposable
{
    private static readonly (string Path, byte[] Content)[] Files =
    [
        ("project.binary", [.. "ECFG"u8, 0, 0, 0, 0]),
        ("ui/menu.tscn", Encoding.UTF8.GetBytes("[gd_scene format=2]\n")),
        ("sound/жук.ogg", [1, 2, 3, 4, 5, 6, 7]),
    ];

    private static readonly byte[] Stub = Encoding.ASCII.GetBytes("MZ stub executable");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string WritePack(uint format, uint flags = 2, IReadOnlySet<string>? encrypted = null)
    {
        _dir.File("Game.pck", GodotFixture.Pack(format, format == 1 ? 3u : 4u, Files, flags: flags, encrypted: encrypted));
        return _dir.Combine("Game.pck");
    }

    private string WriteEmbedded(uint format, uint flags = 2)
    {
        var pack = GodotFixture.Pack(format, format == 1 ? 3u : 4u, Files, start: Stub.Length, flags: flags);
        _dir.File("Game.exe", [.. Stub, .. pack, .. BitConverter.GetBytes((ulong)pack.Length), .. "GDPC"u8]);
        return _dir.Combine("Game.exe");
    }

    private static Dictionary<string, byte[]> Contents(GodotPack pack) => pack.Entries.ToDictionary(e => e.Path, e => pack.Read(e));

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 2u)]
    [InlineData(2u, 0u)]
    [InlineData(3u, 2u)]
    public void Open_reads_every_file_of_a_pack_format(uint format, uint flags)
    {
        // Arrange
        var path = WritePack(format, flags);

        // Act
        using var pack = GodotPack.Open(path);

        // Assert
        (pack.Format, pack.Embedded, Contents(pack)).Should().BeEquivalentTo((format, false, Files.ToDictionary(f => f.Path, f => f.Content)));
    }

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 0u)]
    [InlineData(2u, 2u)]
    [InlineData(3u, 2u)]
    public void Open_finds_a_pack_embedded_at_the_end_of_the_executable(uint format, uint flags)
    {
        // Arrange
        var path = WriteEmbedded(format, flags);

        // Act
        using var pack = GodotPack.Open(path);

        // Assert
        (pack.Embedded, pack.Start, Contents(pack)).Should().BeEquivalentTo((true, (long)Stub.Length, Files.ToDictionary(f => f.Path, f => f.Content)));
    }

    [Fact]
    public void Open_reads_the_engine_version_from_the_header()
    {
        // Arrange
        var path = WritePack(1);

        // Act
        using var pack = GodotPack.Open(path);

        // Assert
        (pack.Major, pack.Minor, pack.Patch).Should().Be((3u, 5u, 1u));
    }

    [Fact]
    public void Encrypted_directory_is_refused()
    {
        // Arrange
        var path = WritePack(2, flags: 2 | 1);

        // Act
        var act = () => GodotPack.Open(path);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Encrypted);
    }

    [Fact]
    public void Sparse_bundle_is_refused()
    {
        // Arrange
        var path = WritePack(3, flags: 2 | 4);

        // Act
        var act = () => GodotPack.Open(path);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Unsupported);
    }

    [Fact]
    public void Encrypted_file_is_listed_as_encrypted()
    {
        // Arrange
        var path = WritePack(2, encrypted: new HashSet<string> { "ui/menu.tscn" });

        // Act
        using var pack = GodotPack.Open(path);

        // Assert
        pack.Entries.Where(e => e.Encrypted).Select(e => e.Path).Should().Equal("ui/menu.tscn");
    }

    [Fact]
    public void A_file_without_the_magic_is_not_a_pack()
    {
        // Arrange
        _dir.File("Game.pck", "AKPK sound bank");

        // Act
        var act = () => GodotPack.Open(_dir.Combine("Game.pck"));

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.NotPack);
    }

    [Fact]
    public void A_file_lying_past_the_end_is_refused()
    {
        // Arrange
        var pack = GodotFixture.Pack(1, 3, Files);
        _dir.File("Game.pck", pack[..^12]);

        // Act
        var act = () => GodotPack.Open(_dir.Combine("Game.pck"));

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Corrupt);
    }

    [Fact]
    public void A_directory_cut_short_is_refused()
    {
        // Arrange
        _dir.File("Game.pck", GodotFixture.Pack(1, 3, Files)[..100]);

        // Act
        var act = () => GodotPack.Open(_dir.Combine("Game.pck"));

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Corrupt);
    }

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 0u)]
    [InlineData(2u, 2u)]
    [InlineData(3u, 2u)]
    public void Write_replaces_a_file_and_keeps_the_others(uint format, uint flags)
    {
        // Arrange
        using var pack = GodotPack.Open(WritePack(format, flags));
        var replaced = Encoding.UTF8.GetBytes("[gd_scene format=2]\n[node name=\"Новое\"]\n");
        using (var output = File.Create(_dir.Combine("New.pck")))
        {
            pack.Write(output, new Dictionary<string, byte[]> { ["ui/menu.tscn"] = replaced }, TestContext.Current.CancellationToken);
        }

        // Act
        using var reread = GodotPack.Open(_dir.Combine("New.pck"));

        // Assert
        (reread.Format, Contents(reread)).Should().BeEquivalentTo((format, new Dictionary<string, byte[]>
        {
            ["project.binary"] = Files[0].Content,
            ["ui/menu.tscn"] = replaced,
            ["sound/жук.ogg"] = Files[2].Content,
        }));
    }

    [Fact]
    public void Write_updates_the_md5_of_a_replaced_file()
    {
        // Arrange
        using var pack = GodotPack.Open(WritePack(2));
        var replaced = "changed"u8.ToArray();
        using (var output = File.Create(_dir.Combine("New.pck")))
        {
            pack.Write(output, new Dictionary<string, byte[]> { ["ui/menu.tscn"] = replaced }, TestContext.Current.CancellationToken);
        }

        // Act
        using var reread = GodotPack.Open(_dir.Combine("New.pck"));

        // Assert
        reread.Entries.Single(e => e.Path == "ui/menu.tscn").Md5.Should().Equal(MD5.HashData(replaced));
    }

    [Fact]
    public void Write_keeps_the_names_as_stored()
    {
        // Arrange
        using var pack = GodotPack.Open(WritePack(1));
        using (var output = File.Create(_dir.Combine("New.pck")))
        {
            pack.Write(output, new Dictionary<string, byte[]>(), TestContext.Current.CancellationToken);
        }

        // Act
        using var reread = GodotPack.Open(_dir.Combine("New.pck"));

        // Assert
        reread.Entries.Select(e => Encoding.UTF8.GetString(e.RawPath)).Should().Equal(pack.Entries.Select(e => Encoding.UTF8.GetString(e.RawPath)));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Write_of_an_embedded_pack_keeps_the_executable_and_ends_with_the_trailer(uint format)
    {
        // Arrange
        using var pack = GodotPack.Open(WriteEmbedded(format));
        var replaced = Encoding.UTF8.GetBytes(new string('x', 500));
        using (var output = File.Create(_dir.Combine("New.exe")))
        {
            pack.Write(output, new Dictionary<string, byte[]> { ["ui/menu.tscn"] = replaced }, TestContext.Current.CancellationToken);
        }

        // Act
        using var reread = GodotPack.Open(_dir.Combine("New.exe"));

        // Assert
        (File.ReadAllBytes(_dir.Combine("New.exe"))[..Stub.Length], reread.Embedded, reread.Read(reread.Entries[1]))
            .Should().BeEquivalentTo((Stub, true, replaced));
    }

    [Fact]
    public void Write_pads_a_shrunk_embedded_pack_to_its_old_length()
    {
        // Arrange
        var path = WriteEmbedded(2);
        var before = new FileInfo(path).Length;
        using var pack = GodotPack.Open(path);
        using (var output = File.Create(_dir.Combine("New.exe")))
        {
            pack.Write(output, new Dictionary<string, byte[]> { ["ui/menu.tscn"] = [] }, TestContext.Current.CancellationToken);
        }

        // Act
        var after = new FileInfo(_dir.Combine("New.exe")).Length;

        // Assert
        after.Should().BeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void IsPack_tells_a_pack_from_another_file()
    {
        // Arrange
        var pack = WritePack(1);
        _dir.File("other.pck", "AKPK");

        // Act
        var result = (GodotPack.IsPack(pack), GodotPack.IsPack(_dir.Combine("other.pck")), GodotPack.IsPack(_dir.Combine("missing.pck")));

        // Assert
        result.Should().Be((true, false, false));
    }
}
