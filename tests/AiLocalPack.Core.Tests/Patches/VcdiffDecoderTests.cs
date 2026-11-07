using AiLocalPack.Core.Patches;

namespace AiLocalPack.Core.Tests.Patches;

public sealed class VcdiffDecoderTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "vcdiff", name);

    private static byte[] Source => File.ReadAllBytes(Fixture("source.bin"));

    private static byte[] Target => File.ReadAllBytes(Fixture("target.bin"));

    private static byte[] Decode(byte[]? source, byte[] delta, VcdiffOptions? options = null)
    {
        using var output = new MemoryStream();
        VcdiffDecoder.Decode(source is null ? null : new MemoryStream(source), new MemoryStream(delta), output, options, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static byte[] Varint(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        for (value >>= 7; value > 0; value >>= 7)
        {
            bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
        }

        return [.. bytes];
    }

    /// <summary>A window with the default code table and no checksum.</summary>
    private static byte[] Window(byte indicator, (long Length, long Position)? segment, int targetLength, byte[] data, byte[] instructions, byte[] addresses)
    {
        byte[] encoding = [.. Varint(targetLength), 0, .. Varint(data.Length), .. Varint(instructions.Length), .. Varint(addresses.Length), .. data, .. instructions, .. addresses];
        return [indicator, .. segment is { } s ? [.. Varint(s.Length), .. Varint(s.Position)] : Array.Empty<byte>(), .. Varint(encoding.Length), .. encoding];
    }

    private static readonly byte[] Header = [0xD6, 0xC3, 0xC4, 0x00, 0x00];

    [Theory]
    [InlineData("plain.vcdiff")]
    [InlineData("lzma.vcdiff")]
    [InlineData("windows.vcdiff")]
    [InlineData("lzma-windows.vcdiff")]
    [InlineData("no-checksum.vcdiff")]
    public void An_xdelta3_delta_rebuilds_the_target_from_the_source(string delta)
    {
        // Arrange
        var bytes = File.ReadAllBytes(Fixture(delta));

        // Act
        var result = Decode(Source, bytes);

        // Assert
        result.Should().Equal(Target);
    }

    [Fact]
    public void A_delta_made_without_a_source_needs_none()
    {
        // Arrange
        var bytes = File.ReadAllBytes(Fixture("no-source.vcdiff"));

        // Act
        var result = Decode(null, bytes);

        // Assert
        result.Should().Equal(Target);
    }

    [Fact]
    public void The_result_length_is_returned()
    {
        // Arrange
        using var output = new MemoryStream();

        // Act
        var length = VcdiffDecoder.Decode(new MemoryStream(Source), File.OpenRead(Fixture("lzma.vcdiff")), output, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        length.Should().Be(Target.Length);
    }

    [Fact]
    public void Another_source_of_the_same_length_fails_the_window_checksum()
    {
        // Arrange
        var source = Source;
        source[30_000] ^= 0xFF;
        var delta = File.ReadAllBytes(Fixture("plain.vcdiff"));

        // Act
        var act = () => Decode(source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.ChecksumMismatch);
    }

    [Fact]
    public void A_shorter_source_is_refused()
    {
        // Arrange
        var source = Source[..1000];
        var delta = File.ReadAllBytes(Fixture("plain.vcdiff"));

        // Act
        var act = () => Decode(source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.SourceTooShort);
    }

    [Fact]
    public void A_missing_source_is_refused_when_the_delta_copies_from_one()
    {
        // Arrange
        var delta = File.ReadAllBytes(Fixture("plain.vcdiff"));

        // Act
        var act = () => Decode(null, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.SourceTooShort);
    }

    [Fact]
    public void Djw_secondary_compression_is_unsupported()
    {
        // Arrange
        var delta = File.ReadAllBytes(Fixture("djw.vcdiff"));

        // Act
        var act = () => Decode(Source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Unsupported);
    }

    [Fact]
    public void A_file_that_is_not_vcdiff_is_refused()
    {
        // Arrange
        var delta = "PK\u0003\u0004 not a delta"u8.ToArray();

        // Act
        var act = () => Decode(Source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.NotVcdiff);
    }

    [Fact]
    public void Another_vcdiff_version_is_unsupported()
    {
        // Arrange
        byte[] delta = [0xD6, 0xC3, 0xC4, (byte)'S', 0x00];

        // Act
        var act = () => Decode(Source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Unsupported);
    }

    [Fact]
    public void An_application_code_table_is_unsupported()
    {
        // Arrange
        byte[] delta = [0xD6, 0xC3, 0xC4, 0x00, 0x02];

        // Act
        var act = () => Decode(Source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Unsupported);
    }

    [Theory]
    [InlineData("plain.vcdiff")]
    [InlineData("lzma.vcdiff")]
    public void A_cut_off_delta_is_corrupt(string name)
    {
        // Arrange
        var delta = File.ReadAllBytes(Fixture(name));
        delta = delta[..(delta.Length - 40)];

        // Act
        var act = () => Decode(Source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Corrupt);
    }

    [Fact]
    public void A_damaged_lzma_section_is_corrupt()
    {
        // Arrange: the xz magic of the first compressed section broken.
        var delta = File.ReadAllBytes(Fixture("lzma.vcdiff"));
        ReadOnlySpan<byte> xz = [0xFD, 0x37, 0x7A, 0x58, 0x5A];
        var magic = delta.AsSpan().IndexOf(xz);
        delta[magic + 1] = 0;

        // Act
        var act = () => Decode(Source, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Corrupt);
    }

    [Fact]
    public void A_target_window_copies_from_the_output_already_written_and_overlaps_itself()
    {
        // Arrange: "abc", then a window over that output copying 6 bytes from its start (3 from the segment, 3 from itself).
        byte[] delta =
        [
            .. Header,
            .. Window(0, null, 3, "abc"u8.ToArray(), [4], []),
            .. Window(0x02, (3, 0), 6, [], [19, 6], [0]),
        ];

        // Act
        var result = Decode(null, delta);

        // Assert
        result.Should().Equal("abcabcabc"u8.ToArray());
    }

    [Fact]
    public void A_run_repeats_one_byte()
    {
        // Arrange: RUN with an explicit size of 5.
        byte[] delta = [.. Header, .. Window(0, null, 5, [(byte)'z'], [0, 5], [])];

        // Act
        var result = Decode(null, delta);

        // Assert
        result.Should().Equal("zzzzz"u8.ToArray());
    }

    [Fact]
    public void A_copy_from_beyond_what_was_decoded_is_corrupt()
    {
        // Arrange: COPY of 4 bytes from address 2 with nothing decoded yet.
        byte[] delta = [.. Header, .. Window(0, null, 4, [], [20], [2])];

        // Act
        var act = () => Decode(null, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Corrupt);
    }

    [Fact]
    public void Instructions_that_leave_the_window_short_are_corrupt()
    {
        // Arrange: a window of 5 bytes with only 3 added.
        byte[] delta = [.. Header, .. Window(0, null, 5, "abc"u8.ToArray(), [4], [])];

        // Act
        var act = () => Decode(null, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.Corrupt);
    }

    [Fact]
    public void A_result_over_the_limit_is_too_large()
    {
        // Arrange
        var delta = File.ReadAllBytes(Fixture("windows.vcdiff"));

        // Act
        var act = () => Decode(Source, delta, new VcdiffOptions(MaxTargetSize: 20_000));

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.TooLarge);
    }

    [Fact]
    public void A_window_over_the_limit_is_too_large()
    {
        // Arrange
        var delta = File.ReadAllBytes(Fixture("plain.vcdiff"));

        // Act
        var act = () => Decode(Source, delta, new VcdiffOptions(MaxWindowSize: 1024));

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.TooLarge);
    }
}
