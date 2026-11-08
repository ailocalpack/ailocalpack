using System.Buffers.Binary;
using System.Text;
using AiLocalPack.Toolkit.Recipes.Unreal;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace AiLocalPack.Toolkit.Tests.Recipes.Unreal;

/// <summary>Own locres / locmeta readers and writers and the pak writer, cross-read by CUE4Parse (task 37).</summary>
public sealed class UnrealFormatTests
{
    private static readonly Dictionary<(string, string), string> Changed = new() { [("Menu", "Play")] = "Играть 日本語 🚀" };

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Locres_written_with_a_translation_reads_back_with_the_same_identities(byte version)
    {
        // Arrange
        var resource = UnrealFixture.Resource(version);

        // Act
        var read = UnrealLocres.Read(resource.Write(Changed));

        // Assert
        read.Entries.Select(e => (e.Namespace, e.Key, e.SourceHash, e.Text)).Should().BeEquivalentTo(
            resource.Entries.Select(e => (e.Namespace, e.Key, e.SourceHash, e.Key == "Play" ? Changed[("Menu", "Play")] : e.Text)));
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Locres_with_hashed_keys_keeps_the_namespace_and_key_hashes(byte version)
    {
        // Arrange
        var resource = UnrealFixture.Resource(version);

        // Act
        var read = UnrealLocres.Read(resource.Write(Changed));

        // Assert
        read.Entries.Select(e => (e.NamespaceHash, e.KeyHash)).Should().BeEquivalentTo(resource.Entries.Select(e => (e.NamespaceHash, e.KeyHash)));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Locres_written_with_a_translation_is_read_the_same_by_CUE4Parse(byte version)
    {
        // Arrange
        var bytes = UnrealFixture.Resource(version).Write(Changed);
        using var archive = new FByteArchive("Game.locres", bytes);

        // Act
        var cue = new FTextLocalizationResource(archive);

        // Assert
        cue.Entries.SelectMany(n => n.Value.Select(k => (n.Key.Str, k.Key.Str, k.Value.SourceStringHash, k.Value.LocalizedString)))
            .Should().BeEquivalentTo(UnrealLocres.Read(bytes).Entries.Select(e => (e.Namespace, e.Key, e.SourceHash, e.Text)));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Every_truncation_of_a_locres_is_refused(byte version)
    {
        // Arrange
        var bytes = UnrealFixture.Resource(version).Write();

        // Act
        var failures = Enumerable.Range(0, bytes.Length).Count(length => Fails(() => UnrealLocres.Read(bytes[..length])));

        // Assert
        failures.Should().Be(bytes.Length);
    }

    [Fact]
    public void A_future_locres_version_is_refused()
    {
        // Arrange
        var bytes = UnrealFixture.Resource(3).Write();
        bytes[16] = 4;

        // Act
        var act = () => UnrealLocres.Read(bytes);

        // Assert
        act.Should().Throw<InvalidDataException>().WithMessage("*version 4*");
    }

    [Fact]
    public void A_string_table_offset_past_the_end_is_refused()
    {
        // Arrange
        var bytes = UnrealFixture.Resource(1).Write();
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(17), long.MaxValue);

        // Act
        var act = () => UnrealLocres.Read(bytes);

        // Assert
        act.Should().Throw<InvalidDataException>().WithMessage("*offset*");
    }

    [Fact]
    public void A_string_index_outside_the_table_is_refused()
    {
        // Arrange
        var bytes = UnrealFixture.Resource(1).Write();
        var table = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(17));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table - 4), -1);

        // Act
        var act = () => UnrealLocres.Read(bytes);

        // Assert
        act.Should().Throw<InvalidDataException>().WithMessage("*index*");
    }

    [Fact]
    public void Legacy_locres_with_ANSI_strings_and_an_empty_namespace_reads()
    {
        // Arrange
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(1);
            writer.Write(0);
            writer.Write(1);
            writer.Write(4);
            writer.Write("Key\0"u8);
            writer.Write(42u);
            writer.Write(6);
            writer.Write("Hello\0"u8);
        }

        // Act
        var read = UnrealLocres.Read(stream.ToArray());

        // Assert
        read.Entries.Should().Equal(new UnrealText("", "Key", 0, 0, 42, "Hello"));
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void Empty_locres_without_a_string_table_reads(byte version)
    {
        // Arrange
        var bytes = new UnrealLocres(version, []).Write()[..^4];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(17), -1);

        // Act
        var read = UnrealLocres.Read(bytes);

        // Assert
        read.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void Locmeta_reads_like_CUE4Parse_reads_it(byte version)
    {
        // Arrange
        var bytes = UnrealFixture.Metadata(version);
        using var archive = new FByteArchive("Game.locmeta", bytes);
        var cue = new FTextLocalizationMetaDataResource(archive);

        // Act
        var read = UnrealLocmeta.Read(bytes);

        // Assert
        (read.NativeCulture, read.NativeLocres, read.CompiledCultures).Should().BeEquivalentTo((cue.NativeCulture, cue.NativeLocRes, cue.CompiledCultures ?? []));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void Every_truncation_of_a_locmeta_is_refused(byte version)
    {
        // Arrange
        var bytes = UnrealFixture.Metadata(version);

        // Act
        var failures = Enumerable.Range(0, bytes.Length).Count(length => Fails(() => UnrealLocmeta.Read(bytes[..length])));

        // Assert
        failures.Should().Be(bytes.Length);
    }

    [Fact]
    public void Pak_files_are_read_back_by_CUE4Parse()
    {
        // Arrange
        using var dir = new TempDir();
        var files = new Dictionary<string, byte[]> { [UnrealFixture.English] = UnrealFixture.Resource(3).Write(), [UnrealFixture.Meta] = UnrealFixture.Metadata() };
        dir.File("test_P.pak", UnrealPakWriter.Write(files));
        using var provider = new DefaultFileProvider(dir.Path, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_27));
        provider.Initialize();
        provider.Mount();

        // Act
        var read = provider.Files.ToDictionary(p => p.Key, p => p.Value.Read());

        // Assert
        read.Should().BeEquivalentTo(files);
    }

    [Fact]
    public void Pak_output_does_not_depend_on_the_order_files_are_given_in()
    {
        // Arrange
        var files = new Dictionary<string, byte[]> { [UnrealFixture.English] = UnrealFixture.Resource(3).Write(), [UnrealFixture.Meta] = UnrealFixture.Metadata() };

        // Act
        var reversed = UnrealPakWriter.Write(files.Reverse().ToDictionary());

        // Assert
        reversed.Should().Equal(UnrealPakWriter.Write(files));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Sample/../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("Sample\\escape")]
    public void Pak_writer_refuses_unsafe_paths(string path)
    {
        // Arrange
        var files = new Dictionary<string, byte[]> { [path] = [1] };

        // Act
        var act = () => UnrealPakWriter.Write(files);

        // Assert
        act.Should().Throw<InvalidDataException>();
    }

    private static bool Fails(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return true;
        }
    }
}
