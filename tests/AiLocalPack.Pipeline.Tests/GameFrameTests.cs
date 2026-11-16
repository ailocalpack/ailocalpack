using AiLocalPack.Core.Games;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Screenshots of the test run: what counts as a picture with something on it, the PNG they are saved as, the baselines kept per game version.</summary>
public sealed class GameFrameTests : IDisposable
{
    private static readonly Game Lantern = new(
        GameId.Steam(620), "Lantern", GameSource.Steam, "/games/lantern", null, GameVersion.FromBuildId("100"), null, "none", null, true);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static GameFrame Frame(Func<int, int, byte> value, int size = 64)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value(x, y);
                pixels[i + 3] = 255;
            }
        }

        return new GameFrame(size, size, pixels);
    }

    [Fact]
    public void A_one_colour_window_has_no_content()
    {
        // Arrange
        var frame = Frame((_, _) => 12);

        // Act
        var content = frame.HasContent;

        // Assert
        content.Should().BeFalse();
    }

    [Fact]
    public void A_window_with_light_and_dark_parts_has_content()
    {
        // Arrange
        var frame = Frame((x, _) => x < 32 ? (byte)10 : (byte)200);

        // Act
        var content = frame.HasContent;

        // Assert
        content.Should().BeTrue();
    }

    [Fact]
    public void A_frame_is_saved_as_a_png_scaled_down_to_the_width_asked()
    {
        // Arrange
        var frame = Frame((x, y) => (byte)((x + y) % 256), size: 64);
        var path = _dir.Combine("shot.png");

        // Act
        frame.SavePng(path, maxWidth: 32);

        // Assert
        var bytes = File.ReadAllBytes(path);
        new { Signature = bytes[..8], Width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)) }
            .Should().BeEquivalentTo(new { Signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, Width = 32 });
    }

    [Fact]
    public void A_baseline_of_another_game_version_doesnt_count()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));
        baselines.Save(Lantern.Id, new TestRunBaseline(TestRunBaselines.VersionKey(Lantern), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(50), true, TimeSpan.FromSeconds(50), DateTimeOffset.UnixEpoch));

        // Act
        var found = baselines.Find(Lantern with { Version = GameVersion.FromBuildId("101") });

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void A_saved_baseline_reads_back()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));
        var baseline = new TestRunBaseline(TestRunBaselines.VersionKey(Lantern), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(50), true, TimeSpan.FromSeconds(50), DateTimeOffset.UnixEpoch);
        baselines.Save(Lantern.Id, baseline);

        // Act
        var found = baselines.Find(Lantern);

        // Assert
        found.Should().Be(baseline);
    }
}
