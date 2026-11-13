using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Requests;

/// <summary><c>requests.json</c>, the index's <c>requests</c> and <c>index build --requests</c> (task 74).</summary>
public sealed class RegistryRequestsTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly RequestKey French = new(GameId.Steam(620), "fr");
    private static readonly RegistryRequest Open = new(
        13,
        new Uri("https://github.com/ailocalpack/registry/discussions/40"),
        new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
        new RegistryRequestClaim("alice", RequestClaimState.Working, GenerationStage.Translate, 45, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)));

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void File_round_trips()
    {
        // Arrange
        var text = RegistryRequests.Write(new Dictionary<RequestKey, RegistryRequest> { [French] = Open });

        // Act
        var (requests, errors) = RegistryRequests.Read(text);

        // Assert
        (requests![French], errors.Count).Should().Be((Open, 0));
    }

    [Fact]
    public void File_ends_with_one_newline()
    {
        // Act
        var text = RegistryRequests.Write(new Dictionary<RequestKey, RegistryRequest> { [French] = Open });

        // Assert
        (text.EndsWith("}\n", StringComparison.Ordinal), text.Contains('\r', StringComparison.Ordinal)).Should().Be((true, false));
    }

    [Fact]
    public void File_with_a_bad_entry_is_refused()
    {
        // Act
        var (requests, errors) = RegistryRequests.Read("""{"schemaVersion":1,"requests":{"steam:620/fr":{"votes":0,"discussion":"https://x/1","opened":"2026-11-01T10:00:00Z"}}}""");

        // Assert
        (requests, errors.Single().Field).Should().Be((null, "requests[\"steam:620/fr\"].votes"));
    }

    [Fact]
    public void Request_with_a_stopped_claim_is_refused()
    {
        // Act
        var (requests, _) = RegistryRequests.Read("""
            {"schemaVersion":1,"requests":{"steam:620/fr":{"votes":1,"discussion":"https://x/1","opened":"2026-11-01T10:00:00Z",
             "claim":{"by":"alice","state":"stopped","percent":0,"updated":"2026-11-01T10:00:00Z"}}}}
            """);

        // Assert
        requests.Should().BeNull();
    }

    [Fact]
    public void Index_round_trips_a_request()
    {
        // Arrange
        var index = RegistryFixtures.Index(RegistryFixtures.Pack());
        var games = index.Games.ToDictionary(g => g.Key, g => g.Value with { Requests = new Dictionary<string, RegistryRequest> { ["fr"] = Open } });

        // Act
        var read = RegistryIndexSerializer.Read(RegistryIndexSerializer.Write(index with { Games = games }));

        // Assert
        read.Index!.RequestFor(GameId.Steam(620), "fr").Should().Be(Open);
    }

    [Fact]
    public void Index_with_a_request_matches_the_schema()
    {
        // Arrange
        var index = RegistryFixtures.Index(RegistryFixtures.Pack());
        var games = index.Games.ToDictionary(g => g.Key, g => g.Value with { Requests = new Dictionary<string, RegistryRequest> { ["fr"] = Open } });

        // Act
        var errors = RegistryFixtures.SchemaErrors(RegistryIndexSerializer.Write(index with { Games = games }));

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Damaged_request_in_the_index_is_left_out_and_the_rest_kept()
    {
        // Arrange
        var json = RegistryFixtures.IndexJson(RegistryFixtures.Pack()).Replace(
            "\"packs\": {",
            "\"requests\": {\"fr\": {\"votes\": -1}, \"de\": {\"votes\": 2, \"discussion\": \"https://x/2\", \"opened\": \"2026-11-01T10:00:00Z\"}},\n      \"packs\": {",
            StringComparison.Ordinal);

        // Act
        var read = RegistryIndexSerializer.Read(json);

        // Assert
        (read.Index!.RequestFor(GameId.Steam(620), "fr"), read.Index.RequestFor(GameId.Steam(620), "de")?.Votes, read.Index.PackCount).Should().Be((null, 2, 1));
    }

    [Fact]
    public void Build_adds_open_requests_to_their_games()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, cancellationToken: TestContext.Current.CancellationToken,
            requests: new Dictionary<RequestKey, RegistryRequest> { [French] = Open, [new RequestKey(GameId.Steam(700), "ru")] = Open with { Claim = null } });

        // Assert
        (result.Index!.RequestFor(GameId.Steam(620), "fr"), result.Index.RequestFor(GameId.Steam(700), "ru")?.Votes).Should().Be((Open, 13));
    }

    [Fact]
    public void Build_leaves_out_a_request_whose_language_has_a_pack()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, cancellationToken: TestContext.Current.CancellationToken,
            requests: new Dictionary<RequestKey, RegistryRequest> { [new RequestKey(GameId.Steam(620), "ru")] = Open });

        // Assert
        (result.Index!.RequestFor(GameId.Steam(620), "ru"), result.Problems.Single().Message).Should().Be((null, "request steam:620/ru has a pack now; left out."));
    }

    [Fact]
    public void Build_leaves_out_a_request_of_an_opted_out_game()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, cancellationToken: TestContext.Current.CancellationToken,
            requests: new Dictionary<RequestKey, RegistryRequest> { [new RequestKey(GameId.Steam(400), "ru")] = Open });

        // Assert
        result.Index!.RequestFor(GameId.Steam(400), "ru").Should().BeNull();
    }
}
