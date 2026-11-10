using System.Text;
using AiLocalPack.Core.Patches;

namespace AiLocalPack.Core.Tests.Patches;

public sealed class VcdiffEncoderTests
{
    private static readonly VcdiffEncoderOptions Small = new(WindowSize: 4096, MaxSegmentSize: 16384);

    private static byte[] Encode(byte[] source, byte[] target, VcdiffEncoderOptions? options = null)
    {
        using var delta = new MemoryStream();
        VcdiffEncoder.Encode(new MemoryStream(source), new MemoryStream(target), delta, options, TestContext.Current.CancellationToken);
        return delta.ToArray();
    }

    private static byte[] Decode(byte[] source, byte[] delta)
    {
        using var output = new MemoryStream();
        VcdiffDecoder.Decode(new MemoryStream(source), new MemoryStream(delta), output, cancellationToken: TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static TheoryData<string> Cases => ["identical", "edited", "inserted", "deleted", "empty-source", "empty-target", "unrelated", "short"];

    private static (byte[] Source, byte[] Target) Case(string name)
    {
        var source = Random(20_000, 1);
        return name switch
        {
            "identical" => (source, source),
            "edited" => (source, [.. source[..5000], .. "translated"u8, .. source[5010..]]),
            "inserted" => (source, [.. source[..7000], .. Random(300, 2), .. source[7000..]]),
            "deleted" => (source, [.. source[..3000], .. source[9000..]]),
            "empty-source" => ([], source),
            "empty-target" => (source, []),
            "unrelated" => (source, Random(10_000, 3)),
            _ => ("abc"u8.ToArray(), "abd"u8.ToArray()),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decoding_the_delta_gives_the_target_back(string name)
    {
        // Arrange
        var (source, target) = Case(name);
        var delta = Encode(source, target);

        // Act
        var decoded = Decode(source, delta);

        // Assert
        decoded.Should().Equal(target);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Small_windows_and_a_sliding_source_segment_still_decode_to_the_target(string name)
    {
        // Arrange
        var (source, target) = Case(name);
        source = [.. source, .. source, .. Random(30_000, 4)];
        target = [.. target, .. source[20_000..]];
        var delta = Encode(source, target, Small);

        // Act
        var decoded = Decode(source, delta);

        // Assert
        decoded.Should().Equal(target);
    }

    [Fact]
    public void A_small_edit_in_a_large_file_makes_a_small_delta()
    {
        // Arrange
        var source = Random(1_000_000, 5);
        var target = source.ToArray();
        Encoding.UTF8.GetBytes("Привет").CopyTo(target, 500_000);

        // Act
        var delta = Encode(source, target);

        // Assert
        delta.Length.Should().BeLessThan(200);
    }

    [Fact]
    public void An_insertion_early_in_a_file_larger_than_the_segment_keeps_matching()
    {
        // Arrange
        var source = Random(100_000, 6);
        byte[] target = [.. source[..1000], .. Random(5000, 7), .. source[1000..]];

        // Act
        var delta = Encode(source, target, Small);

        // Assert
        delta.Length.Should().BeLessThan(10_000);
    }

    [Fact]
    public void The_same_files_give_the_same_delta()
    {
        // Arrange
        var (source, target) = Case("inserted");
        var first = Encode(source, target);

        // Act
        var second = Encode(source, target);

        // Assert
        second.Should().Equal(first);
    }

    [Fact]
    public void The_delta_is_checked_against_another_source_by_its_window_checksum()
    {
        // Arrange
        var (source, target) = Case("edited");
        var delta = Encode(source, target);
        var other = Random(source.Length, 99);

        // Act
        var act = () => Decode(other, delta);

        // Assert
        act.Should().Throw<VcdiffException>().Which.Problem.Should().Be(VcdiffProblem.ChecksumMismatch);
    }

    [Fact]
    public void A_source_that_cannot_seek_is_refused()
    {
        // Arrange
        using var source = new NonSeekable();

        // Act
        var act = () => VcdiffEncoder.Encode(source, new MemoryStream([1, 2]), new MemoryStream());

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    private sealed class NonSeekable : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
