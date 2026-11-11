using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Activity;

/// <summary>The Activity store (task 38): decisions, feed, tasks, persistence.</summary>
public sealed class ActivityStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _home = new();
    private readonly AppPaths _paths;
    private readonly FakeClock _clock = new(Noon);

    public ActivityStoreTests() => _paths = AppPaths.UnderHome(_home.Path);

    public void Dispose() => _home.Dispose();

    private ActivityStore Store() => new(_paths, _clock);

    private static ActivityEvent Event(string id, ActivityEventKind kind = ActivityEventKind.PackInstalled, int minutesAgo = 0, string? key = null) =>
        ActivityEvent.Create(kind, Noon.AddMinutes(-minutesAgo)) with
        {
            Id = id,
            Game = GameId.Steam(620),
            GameName = "Portal 2",
            PackId = "full",
            PackTitle = "Полный перевод",
            Revision = 3,
            Key = key,
        };

    private static ActivityEvent Decision(string id, string? key = null) => Event(id, ActivityEventKind.PackIncompatible, key: key);

    [Fact]
    public void Decision_kinds_start_as_decisions()
    {
        // Act
        var activity = ActivityEvent.Create(ActivityEventKind.AutoReapplyFailed, Noon);

        // Assert
        (activity.IsDecision, activity.Tone).Should().Be((true, ActivityTone.Bad));
    }

    [Fact]
    public void Events_split_into_decisions_and_feed()
    {
        // Arrange
        var store = Store();
        store.Add(Event("installed"));

        // Act
        store.Add(Decision("broken"));

        // Assert
        (store.Decisions.Select(e => e.Id), store.Feed.Select(e => e.Id)).Should().BeEquivalentTo((new[] { "broken" }, new[] { "installed" }));
    }

    [Fact]
    public void Read_all_never_clears_decisions()
    {
        // Arrange
        var store = Store();
        store.Add(Event("installed"));
        store.Add(Decision("broken"));

        // Act
        store.MarkAllRead();

        // Assert
        store.Events.Select(e => (e.Id, e.IsDecision, e.IsRead)).Should().BeEquivalentTo(new[]
        {
            ("installed", false, true),
            ("broken", true, false),
        });
    }

    [Fact]
    public void Read_all_leaves_the_amber_badge()
    {
        // Arrange
        var store = Store();
        store.Add(Event("installed"));
        store.Add(Decision("broken"));

        // Act
        store.MarkAllRead();

        // Assert
        store.Badge.Should().Be(new ActivityBadge(1, ActivityBadgeTone.Attention));
    }

    [Fact]
    public void Decide_later_moves_the_decision_into_the_feed_read()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("broken"));

        // Act
        store.DecideLater("broken");

        // Assert
        (store.Decisions.Count, store.Feed.Single().IsRead).Should().Be((0, true));
    }

    [Fact]
    public void Decision_can_not_be_hidden()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("broken"));

        // Act
        store.Hide("broken");

        // Assert
        store.Decisions.Should().ContainSingle();
    }

    [Fact]
    public void Hidden_event_leaves_the_feed()
    {
        // Arrange
        var store = Store();
        store.Add(Event("installed"));

        // Act
        store.Hide("installed");

        // Assert
        store.Feed.Should().BeEmpty();
    }

    [Fact]
    public void Same_key_is_added_once_even_after_decide_later()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("first", key: "pack-Incompatible:steam:620"));
        store.DecideLater("first");

        // Act
        var added = store.Add(Decision("second", key: "pack-Incompatible:steam:620"));

        // Assert
        (added, store.Decisions.Count).Should().Be((false, 0));
    }

    [Fact]
    public void Same_key_is_added_once_after_hiding()
    {
        // Arrange
        var store = Store();
        store.Add(Event("first", key: "k"));
        store.Hide("first");

        // Act
        var added = store.Add(Event("second", key: "k"));

        // Assert
        added.Should().BeFalse();
    }

    [Fact]
    public void Resolve_game_settles_only_the_matching_decisions()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("portal"));
        store.Add(Decision("other") with { Game = GameId.Steam(10) });

        // Act
        store.ResolveGame(GameId.Steam(620), _ => true);

        // Assert
        store.Decisions.Select(e => e.Id).Should().Equal("other");
    }

    [Fact]
    public void Mark_read_leaves_a_decision_unread()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("broken"));

        // Act
        store.MarkRead("broken");

        // Assert
        store.Decisions.Single().IsRead.Should().BeFalse();
    }

    [Fact]
    public void Events_round_trip_through_the_file()
    {
        // Arrange
        var failed = Event("failed", ActivityEventKind.AutoReapplyFailed, minutesAgo: 5, key: "k") with
        {
            Detail = "data/Map001.json is in use.",
            Log = ["12:00:01 Checking", "  OS: in use"],
            FilesRestored = false,
            CanRetry = true,
        };
        var store = Store();
        store.Add(failed);
        store.Add(Event("installed"));
        store.MarkRead("installed");

        // Act
        var read = Store().Events;

        // Assert
        read.Should().BeEquivalentTo(new[] { Event("installed") with { IsRead = true }, failed }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void A_paused_generation_s_resume_time_round_trips()
    {
        // Arrange
        var until = new DateTimeOffset(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);
        Store().Add(Event("paused", ActivityEventKind.GenerationPaused) with { Until = until });

        // Act
        var read = Store().Decisions.Single();

        // Assert
        (read.Kind, read.Until, read.Tone).Should().Be((ActivityEventKind.GenerationPaused, until, ActivityTone.Paused));
    }

    [Fact]
    public void A_rejected_prs_number_and_link_round_trip()
    {
        // Arrange
        var link = new Uri("https://github.com/ailocalpack/registry/pull/7");
        Store().Add(Event("ci", ActivityEventKind.PullRequestFailed) with { PullRequest = 7, Link = link });

        // Act
        var read = Store().Decisions.Single();

        // Assert
        (read.Kind, read.PullRequest, read.Link, read.Tone).Should().Be((ActivityEventKind.PullRequestFailed, (int?)7, link, ActivityTone.Bad));
    }

    [Fact]
    public void Hidden_and_set_aside_state_round_trips()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("broken", key: "k"));
        store.DecideLater("broken");
        store.Add(Event("installed", key: "i"));
        store.Hide("installed");

        // Act
        var added = Store().Add(Event("again", key: "i"));

        // Assert
        (added, Store().Feed.Single().Id, Store().Decisions.Count).Should().Be((false, "broken", 0));
    }

    [Fact]
    public void Running_tasks_are_not_saved()
    {
        // Arrange
        var store = Store();
        store.SetTask(new ActivityTask("t", ActivityTaskKind.Install, GameId.Steam(620), "Portal 2", "Installing", null, 0.5, Noon));

        // Act
        var read = Store();

        // Assert
        (store.Tasks.Count, read.Tasks.Count).Should().Be((1, 0));
    }

    [Fact]
    public void Running_task_updates_in_place_and_ends()
    {
        // Arrange
        var store = Store();
        var task = new ActivityTask("t", ActivityTaskKind.Install, GameId.Steam(620), "Portal 2", "Installing", "Downloading", 0.1, Noon);
        store.SetTask(task);
        store.SetTask(task with { Fraction = 0.6 });

        // Act
        var during = store.Tasks.Single().Fraction;
        store.EndTask("t");

        // Assert
        (during, store.Tasks.Count).Should().Be((0.6, 0));
    }

    [Fact]
    public void Running_task_counts_in_the_badge()
    {
        // Arrange
        var store = Store();
        store.Add(Event("installed"));

        // Act
        store.SetTask(new ActivityTask("t", ActivityTaskKind.Install, GameId.Steam(620), "Portal 2", "Installing", null, null, Noon));

        // Assert
        store.Badge.Should().Be(new ActivityBadge(2, ActivityBadgeTone.Normal));
    }

    [Fact]
    public void Changes_raise_changed()
    {
        // Arrange
        var store = Store();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        // Act
        store.Add(Event("installed"));
        store.MarkAllRead();
        store.MarkAllRead();

        // Assert
        raised.Should().Be(2, "the second Read all changed nothing");
    }

    [Fact]
    public void Feed_keeps_the_newest_events_and_every_decision()
    {
        // Arrange
        var store = Store();
        store.Add(Decision("old decision") with { Time = Noon.AddDays(-30) });
        for (var i = 0; i < ActivityStore.MaxFeed + 5; i++)
        {
            store.Add(Event($"e{i}", minutesAgo: i));
        }

        // Act
        var events = Store().Events;

        // Assert
        (events.Count(e => e.IsInFeed), events.Any(e => e.Id == "e0"), events.Any(e => e.Id == $"e{ActivityStore.MaxFeed}"), events.Any(e => e.IsDecision))
            .Should().Be((ActivityStore.MaxFeed, true, false, true));
    }

    [Fact]
    public void Damaged_file_is_moved_aside()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.ActivityFile, "{ not json");

        // Act
        var events = Store().Events;

        // Assert
        (events.Count, Directory.GetFiles(_paths.Data, "activity.json.*.bak").Length).Should().Be((0, 1));
    }

    [Fact]
    public void Unknown_kinds_and_broken_entries_are_skipped()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.ActivityFile, """
            {
              "schemaVersion": 1,
              "events": [
                { "id": "a", "kind": "packInstalled", "time": "2026-11-08T10:00:00Z", "game": "steam:620" },
                { "id": "b", "kind": "prMerged", "time": "2026-11-08T10:00:00Z" },
                { "id": "c", "kind": "packInstalled", "time": "2026-11-08T10:00:00Z", "game": "not a game" },
                { "kind": "packInstalled", "time": "2026-11-08T10:00:00Z" },
                null
              ]
            }
            """);

        // Act
        var events = Store().Events;

        // Assert
        events.Select(e => e.Id).Should().Equal("a");
    }

    [Fact]
    public void Newer_file_is_read_but_not_overwritten()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        var text = """{ "schemaVersion": 2, "events": [ { "id": "a", "kind": "packInstalled", "time": "2026-11-08T10:00:00Z" } ] }""";
        File.WriteAllText(_paths.ActivityFile, text);
        var store = Store();

        // Act
        store.MarkAllRead();

        // Assert
        (store.Feed.Single().IsRead, File.ReadAllText(_paths.ActivityFile)).Should().Be((true, text));
    }
}
