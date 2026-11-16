using System.Buffers.Binary;
using System.Diagnostics;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The parts of the Linux and macOS probes that need no X server or Screen Recording permission.</summary>
public sealed class UnixGameProbeTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>A 2 × 2 BMP: red, green on the top row, blue, white below.</summary>
    private static byte[] Bmp(int bits, bool topDown)
    {
        var bytesPerPixel = bits / 8;
        var stride = (2 * bytesPerPixel + 3) & ~3;
        var data = new byte[54 + stride * 2];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), topDown ? -2 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), (ushort)bits);
        byte[][] top = [[0, 0, 255], [0, 255, 0]];
        byte[][] bottom = [[255, 0, 0], [255, 255, 255]];
        foreach (var (row, pixels) in new[] { (topDown ? 0 : 1, top), (topDown ? 1 : 0, bottom) })
        {
            for (var x = 0; x < 2; x++)
            {
                pixels[x].CopyTo(data, 54 + row * stride + x * bytesPerPixel);
            }
        }

        return data;
    }

    private static byte[] Rgb(GameFrame frame) =>
        [.. Enumerable.Range(0, frame.Width * frame.Height).SelectMany(i => new[] { frame.Pixels[i * 4 + 2], frame.Pixels[i * 4 + 1], frame.Pixels[i * 4] })];

    [Theory]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void A_bmp_reads_as_its_pixels_top_row_first(int bits, bool topDown)
    {
        // Arrange
        var data = Bmp(bits, topDown);

        // Act
        var frame = Pipeline.Bmp.Read(data);

        // Assert
        Rgb(frame!).Should().Equal(255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255);
    }

    [Fact]
    public void A_file_that_isnt_a_bmp_reads_as_nothing()
    {
        // Arrange
        var data = new byte[100];

        // Act
        var frame = Pipeline.Bmp.Read(data);

        // Assert
        frame.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_x_image_of_rgb_words_reads_as_its_colours_in_either_byte_order(bool lsbFirst)
    {
        // Arrange
        var raw = new byte[8];
        foreach (var (word, i) in new[] { (0x00FF8000u, 0), (0x000000FFu, 1) })
        {
            if (lsbFirst)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i * 4), word);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(i * 4), word);
            }
        }

        // Act
        var frame = XImages.FromPixels(raw, 2, 1, 8, lsbFirst, 0xFF0000);

        // Assert
        Rgb(frame!).Should().Equal(255, 128, 0, 0, 0, 255);
    }

    [Fact]
    public void An_x_image_with_unusual_channel_masks_reads_as_nothing()
    {
        // Arrange
        var raw = new byte[4];

        // Act
        var frame = XImages.FromPixels(raw, 1, 1, 4, lsbFirst: true, redMask: 0xF800);

        // Assert
        frame.Should().BeNull();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "/proc is Linux only.")]
    public void The_process_tree_holds_the_processes_a_process_started()
    {
        // Arrange
        using var child = Process.Start("sleep", "5");

        // Act
        var tree = ProcTree.Of(Environment.ProcessId);

        // Assert
        try
        {
            tree.Should().Contain(child.Id);
        }
        finally
        {
            child.Kill();
        }
    }
}
