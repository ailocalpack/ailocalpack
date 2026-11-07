using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>The re-check after a game update (task 33): file states against the install record, and the decision table.</summary>
public sealed class PackRecheckTests : IDisposable
{
    private const string Map = "data/Map001.json";
    private const string System = "data/System.json";
    private const string Font = "fonts/ailocalpack-noto.ttf";

    private readonly TempDir _game = new();

    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _game.Dispose();

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The pack translated both maps and added a font.</summary>
    private InstalledPack Record() => new(
        GameId.Steam(620),
        "ru",
        "full",
        1,
        new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        _game.Path,
        "backup",
        [
            new InstalledFile(Map, Sha256("map original"), Sha256("map translated")),
            new InstalledFile(System, Sha256("system original"), Sha256("system translated")),
            new InstalledFile(Font, null, Sha256("font")),
        ],
        []);

    private PackFileStates Inspect() => PackRecheck.Inspect(Record(), _game.Path, new FileHashCache(), TestContext.Current.CancellationToken);

    private void Files(string map = "map translated", string system = "system translated", string? font = "font")
    {
        _game.File(Map, map).File(System, system);
        if (font is not null)
        {
            _game.File(Font, font);
        }
    }

    [Fact]
    public void Files_as_the_pack_wrote_them_are_intact()
    {
        // Arrange
        Files();

        // Act
        var states = Inspect();

        // Assert
        states.IsIntact.Should().BeTrue();
    }

    [Fact]
    public void Files_put_back_to_the_original_are_reverted()
    {
        // Arrange
        Files(map: "map original");

        // Act
        var states = Inspect();

        // Assert
        (states.Reverted, states.Changed, states.Missing).Should().BeEquivalentTo((new[] { Map }, Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void Files_rewritten_by_an_update_are_changed()
    {
        // Arrange
        Files(system: "system v2");

        // Act
        var states = Inspect();

        // Assert
        states.Changed.Should().Equal(System);
    }

    [Fact]
    public void Removed_added_file_is_missing()
    {
        // Arrange
        Files(font: null);

        // Act
        var states = Inspect();

        // Assert
        states.Missing.Should().Equal(Font);
    }

    [Fact]
    public void Files_still_as_the_pack_wrote_them_stand_for_their_originals()
    {
        // Arrange
        Files(map: "map original");

        // Act
        var states = Inspect();

        // Assert
        states.Originals.Should().BeEquivalentTo(new Dictionary<string, string> { [System] = Sha256("system original") });
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Unix permissions")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Unreadable_file_is_reported()
    {
        // Arrange
        Files(map: "map original");
        var path = _game.Combine(Map);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            // Act
            var states = Inspect();

            // Assert
            states.Unreadable.Should().Equal(Map);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public enum FilesCase
    {
        Intact,
        Reverted,
        Unreadable,
    }

    private static PackFileStates States(FilesCase files) => files switch
    {
        FilesCase.Intact => new([], [], [], []),
        FilesCase.Reverted => new([Map], [System], [], []),
        _ => new([Map], [], [], [System]),
    };

    [Theory]
    [InlineData(FilesCase.Intact, PackCompatibilityVerdict.Incompatible, true, PackRecheckDecision.Intact)]
    [InlineData(FilesCase.Intact, null, false, PackRecheckDecision.Intact)]
    [InlineData(FilesCase.Unreadable, PackCompatibilityVerdict.Compatible, true, PackRecheckDecision.Unchecked)]
    [InlineData(FilesCase.Reverted, PackCompatibilityVerdict.Compatible, true, PackRecheckDecision.Reapply)]
    [InlineData(FilesCase.Reverted, PackCompatibilityVerdict.ProbablyCompatible, true, PackRecheckDecision.Reapply)]
    [InlineData(FilesCase.Reverted, PackCompatibilityVerdict.Compatible, false, PackRecheckDecision.Wiped)]
    [InlineData(FilesCase.Reverted, null, true, PackRecheckDecision.Wiped)]
    [InlineData(FilesCase.Reverted, PackCompatibilityVerdict.Incompatible, true, PackRecheckDecision.Incompatible)]
    [InlineData(FilesCase.Reverted, PackCompatibilityVerdict.Incompatible, false, PackRecheckDecision.Incompatible)]
    public void Decision_table(FilesCase files, PackCompatibilityVerdict? compatibility, bool autoReapply, PackRecheckDecision expected)
    {
        // Act
        var decision = PackRecheck.Decide(States(files), compatibility, autoReapply);

        // Assert
        decision.Should().Be(expected);
    }
}
