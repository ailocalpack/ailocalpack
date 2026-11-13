using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Requests;

/// <summary>The player's own requests and their jobs' claims, kept in <c>translation-requests.json</c> (task 74).</summary>
public sealed class TranslationRequestStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly RequestKey Key = new(GameId.Steam(620), "ru");
    private static readonly Uri Link = new("https://github.com/ailocalpack/registry/discussions/5");

    private readonly TempDir _home = new();
    private readonly AppPaths _paths;

    public TranslationRequestStoreTests() => _paths = AppPaths.UnderHome(_home.Path);

    public void Dispose() => _home.Dispose();

    private TranslationRequestStore Store() => new(_paths, new FakeClock(Now));

    [Fact]
    public void Asked_request_survives_a_restart()
    {
        // Arrange
        Store().Ask(Key, Link, Now);

        // Act
        var found = Store().Find(Key);

        // Assert
        found.Should().Be(new AskedRequest(Key, Link, Now));
    }

    [Fact]
    public void Withdrawn_request_is_gone()
    {
        // Arrange
        var store = Store();
        store.Ask(Key, Link, Now);

        // Act
        store.Withdraw(Key);

        // Assert
        Store().Find(Key).Should().BeNull();
    }

    [Fact]
    public void Done_is_kept_once()
    {
        // Arrange
        var store = Store();
        store.Ask(Key, Link, Now);
        store.Done(Key, Now);

        // Act
        var found = store.Done(Key, Now.AddDays(1));

        // Assert
        found!.DoneAt.Should().Be(Now);
    }

    [Fact]
    public void Asking_again_starts_done_over()
    {
        // Arrange
        var store = Store();
        store.Ask(Key, Link, Now);
        store.Done(Key, Now);
        store.SeenDone(Key);

        // Act
        var found = store.Ask(Key, null, Now.AddDays(2));

        // Assert
        found.Should().BeEquivalentTo(new { DoneAt = (DateTimeOffset?)null, DoneSeen = false, Discussion = Link, AskedAt = Now });
    }

    [Fact]
    public void Claim_survives_a_restart()
    {
        // Arrange
        var claim = new RequestClaimRecord("job1", Key, Link, "DC_1", new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Translate, 45, "claude", GenerationQuality.Thorough));
        Store().SetClaim(claim);

        // Act
        var found = Store().FindClaim("job1");

        // Assert
        found.Should().Be(claim);
    }

    [Fact]
    public void Removed_claim_is_gone()
    {
        // Arrange
        var store = Store();
        store.SetClaim(new RequestClaimRecord("job1", Key, Link));

        // Act
        store.RemoveClaim("job1");

        // Assert
        Store().Claims().Should().BeEmpty();
    }

    [Fact]
    public void Damaged_file_is_moved_aside_and_read_as_empty()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.TranslationRequestsFile, "{ not json");

        // Act
        var asked = Store().Asked();

        // Assert
        (asked.Count, Directory.GetFiles(_paths.Data, "translation-requests.json.*.bak").Length).Should().Be((0, 1));
    }

    [Fact]
    public void Newer_file_is_never_overwritten()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.TranslationRequestsFile, """{"schemaVersion": 9}""");

        // Act
        Store().Ask(Key, Link, Now);

        // Assert
        File.ReadAllText(_paths.TranslationRequestsFile).Should().Be("""{"schemaVersion": 9}""");
    }
}
