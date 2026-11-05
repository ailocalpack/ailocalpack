using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Tests.Folders;

/// <summary>
/// <c>tests/fixtures/games/sample-quest</c> is the game the registry's sample pack
/// (<c>ailocalpack/registry</c>, <c>games/fp-f362…/ru/sample-quest-ru</c>) was made for: its fingerprint and the hashes of
/// the files the pack touches are written in that pack's manifest, so they must never change.
/// </summary>
public sealed class SampleQuestFixtureTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "games", "sample-quest");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FoundGame Game() => new FolderRecognizer().Scan(Folder, cancellationToken: Ct).Games.Single();

    [Fact]
    public void Fingerprint_IsTheOneTheRegistrySamplePackNames()
    {
        // Arrange
        var game = Game();

        // Act
        var id = new GameFingerprinter().Fingerprint(game, cancellationToken: Ct);

        // Assert
        id.ToString().Should().Be("fp:f362498e21ee992e45352516246500dac24e10b902c54cba2a144c51fe040288");
    }

    [Fact]
    public void Version_HasTheFileHashesOfTheRegistrySamplePack()
    {
        // Arrange
        var game = Game();

        // Act
        var version = new GameFingerprinter().Version(game, cancellationToken: Ct);

        // Assert
        version!.FileHashes.Should().Contain(new Dictionary<string, string>
        {
            ["data/Map001.json"] = "9d597eff8184514083fe724623c8d09cc08caa0dcce5178b9581cecd7e61cf96",
            ["data/System.json"] = "16630e8c150320ca7f723b23c6f152878ca92b489410a4a8136ef116c344cb8b",
        });
    }
}
