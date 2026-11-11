using System.Net;
using System.Text;
using System.Text.Json;
using AiLocalPack.Core.Publishing;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>A pack PR's state and CI result from GitHub's answers (task 62).</summary>
public sealed class PullRequestStatusTests : IDisposable
{
    private const string Api = "https://api.github.example/repos/ailocalpack/registry/";
    private const string Head = "a1b2c3";
    private const string Folder = "games/steam-620/ru/claude-code";

    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly PullRequestStatusClient _status;

    public PullRequestStatusTests()
    {
        _client = _http.CreateClient();
        _status = new PullRequestStatusClient(_client, PublishBed.App, new FakeClock(PublishBed.Now));
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string Pull(string state = "open", bool merged = false, string? mergedAt = null) =>
        $$$"""{"number":7,"state":"{{{state}}}","merged":{{{(merged ? "true" : "false")}}},"merged_at":{{{(mergedAt is null ? "null" : $"\"{mergedAt}\"")}}},"closed_at":"2026-11-10T13:00:00Z","head":{"sha":"{{{Head}}}","ref":"pack/steam-620/ru/claude-code/rev-1"}}""";

    private static string Runs(params string[] runs) => $$"""{"total_count":{{runs.Length}},"check_runs":[{{string.Join(",", runs)}}]}""";

    private static string Run(long id, string status, string? conclusion, int annotations = 0) =>
        $$$"""{"id":{{{id}}},"name":"validate","status":"{{{status}}}","conclusion":{{{(conclusion is null ? "null" : $"\"{conclusion}\"")}}},"output":{"title":null,"annotations_count":{{{annotations}}}}}""";

    private const string Annotations = """
        [
          {"path":"games/steam-620/ru/claude-code/manifest.json","start_line":1,"annotation_level":"failure","title":"status","message":"a new pack or revision must have \"status\": \"alpha\" (found \"verified\")"},
          {"path":".github","start_line":1,"annotation_level":"failure","title":"","message":"Process completed with exit code 1."}
        ]
        """;

    private void Answer(string path, string body, string? etag = null)
    {
        _http.On(HttpMethod.Get, Api + path, request =>
        {
            if (etag is not null && request.Headers.TryGetValues("If-None-Match", out var sent) && sent.Contains(etag))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return response;
        });
    }

    private Task<PullRequestCheckResult> CheckAsync(string? token = "token") =>
        _status.CheckAsync(RegistryRepo.Default, 7, token, TestContext.Current.CancellationToken);

    // ---------- Pull request ----------

    [Fact]
    public void A_merged_pull_request_is_merged_whatever_its_state_says()
    {
        // Act
        var pull = PullRequestStatuses.Pull(Json(Pull("closed", merged: true, mergedAt: "2026-11-10T12:30:00Z")));

        // Assert
        pull.Should().Be((PullRequestState.Merged, Head, (DateTimeOffset?)new DateTimeOffset(2026, 11, 10, 12, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_closed_pull_request_not_merged_is_closed()
    {
        // Act
        var pull = PullRequestStatuses.Pull(Json(Pull("closed")));

        // Assert
        pull.State.Should().Be(PullRequestState.Closed);
    }

    [Fact]
    public void An_open_pull_request_is_open()
    {
        // Act
        var pull = PullRequestStatuses.Pull(Json(Pull()));

        // Assert
        pull.Should().Be((PullRequestState.Open, Head, (DateTimeOffset?)null));
    }

    // ---------- Check runs ----------

    [Fact]
    public void A_run_still_going_is_pending()
    {
        // Act
        var ci = PullRequestStatuses.Ci(Json(Runs(Run(1, "completed", "success"), Run(2, "in_progress", null))));

        // Assert
        ci.State.Should().Be(CiState.Pending);
    }

    [Fact]
    public void No_runs_yet_is_pending()
    {
        // Act
        var ci = PullRequestStatuses.Ci(Json(Runs()));

        // Assert
        ci.State.Should().Be(CiState.Pending);
    }

    [Fact]
    public void A_failed_run_fails_with_its_id_and_annotation_count()
    {
        // Act
        var ci = PullRequestStatuses.Ci(Json(Runs(Run(1, "completed", "success"), Run(2, "completed", "failure", 3))));

        // Assert
        (ci.State, ci.Failed).Should().BeEquivalentTo((CiState.Failed, new[] { (2L, 3) }));
    }

    [Fact]
    public void A_timed_out_run_fails()
    {
        // Act
        var ci = PullRequestStatuses.Ci(Json(Runs(Run(2, "completed", "timed_out"))));

        // Assert
        ci.State.Should().Be(CiState.Failed);
    }

    [Fact]
    public void Successful_runs_pass()
    {
        // Act
        var ci = PullRequestStatuses.Ci(Json(Runs(Run(1, "completed", "success"), Run(2, "completed", "skipped"))));

        // Assert
        ci.State.Should().Be(CiState.Passed);
    }

    [Fact]
    public void Only_a_cancelled_run_is_still_pending()
    {
        // Act
        var ci = PullRequestStatuses.Ci(Json(Runs(Run(1, "completed", "cancelled"))));

        // Assert
        ci.State.Should().Be(CiState.Pending);
    }

    // ---------- Annotations ----------

    [Fact]
    public void Annotations_read_path_level_title_and_message()
    {
        // Act
        var annotations = CiAnnotations.Parse(Json(Annotations));

        // Assert
        annotations[0].Should().Be(new CiAnnotation(Folder + "/manifest.json", CiAnnotationLevel.Failure, "status", "a new pack or revision must have \"status\": \"alpha\" (found \"verified\")"));
    }

    [Fact]
    public void Githubs_own_exit_code_line_has_no_path_or_title()
    {
        // Act
        var annotations = CiAnnotations.Parse(Json(Annotations));

        // Assert
        (annotations[1].Path, annotations[1].Title).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public void An_answer_that_is_no_list_has_no_annotations()
    {
        // Act
        var annotations = CiAnnotations.Parse(Json("""{"message":"Not Found"}"""));

        // Assert
        annotations.Should().BeEmpty();
    }

    // ---------- Failure reasons ----------

    private static CiAnnotation Failure(string? path, string? title, string message) => new(path, CiAnnotationLevel.Failure, title, message);

    [Fact]
    public void The_scope_error_is_a_scope_failure()
    {
        // Act
        var failure = CiFailures.Explain([Failure(null, CiFailures.ScopeTitle, "This pull request can't be accepted: README.md is outside games/.")], Folder);

        // Assert
        failure.Kind.Should().Be(CiFailureKind.Scope);
    }

    [Fact]
    public void A_status_error_on_the_manifest_is_a_status_failure()
    {
        // Act
        var failure = CiFailures.Explain(CiAnnotations.Parse(Json(Annotations)), Folder);

        // Assert
        failure.Kind.Should().Be(CiFailureKind.Status);
    }

    [Fact]
    public void A_patch_error_is_a_patch_failure()
    {
        // Act
        var failure = CiFailures.Explain([Failure(Folder + "/manifest.json", "patch", "the archive in your fork doesn't match patch.sha256")], Folder);

        // Assert
        failure.Kind.Should().Be(CiFailureKind.Patch);
    }

    [Fact]
    public void A_validator_error_on_a_pack_file_is_a_pack_failure_worded_with_its_field()
    {
        // Act
        var failure = CiFailures.Explain([Failure(Folder + "/manifest.json", "builds[0].files", "must list at least one file.")], Folder);

        // Assert
        (failure.Kind, failure.FirstProblem).Should().Be((CiFailureKind.Pack, "builds[0].files: must list at least one file"));
    }

    [Fact]
    public void An_error_outside_the_pack_is_the_registrys()
    {
        // Act
        var failure = CiFailures.Explain([Failure("ci/ailocalpack-ref", null, "must hold one full 40-character commit SHA")], Folder);

        // Assert
        failure.Kind.Should().Be(CiFailureKind.Registry);
    }

    [Fact]
    public void Only_the_exit_code_line_is_an_unknown_failure()
    {
        // Act
        var failure = CiFailures.Explain([Failure(null, null, "Process completed with exit code 1.")], Folder);

        // Assert
        (failure.Kind, failure.Problems.Count).Should().Be((CiFailureKind.Unknown, 0));
    }

    [Fact]
    public void Warnings_are_no_reason()
    {
        // Act
        var failure = CiFailures.Explain([new CiAnnotation(Folder + "/strings/strings.json.gz", CiAnnotationLevel.Warning, "too-long", "a: longer than 40")], Folder);

        // Assert
        failure.Kind.Should().Be(CiFailureKind.Unknown);
    }

    // ---------- Client ----------

    [Fact]
    public async Task A_rejected_pull_request_comes_with_its_annotations()
    {
        // Arrange
        Answer("pulls/7", Pull());
        Answer($"commits/{Head}/check-runs?per_page=100", Runs(Run(42, "completed", "failure", 2)));
        Answer("check-runs/42/annotations?per_page=50&page=1", Annotations);

        // Act
        var result = await CheckAsync();

        // Assert
        result.Should().BeOfType<PullRequestCheckResult.Checked>().Which.Status.Should().BeEquivalentTo(new
        {
            State = PullRequestState.Open,
            HeadSha = Head,
            Ci = CiState.Failed,
            Annotations = CiAnnotations.Parse(Json(Annotations)),
        });
    }

    [Fact]
    public async Task A_merged_pull_request_reads_no_check_runs()
    {
        // Arrange
        Answer("pulls/7", Pull("closed", merged: true, mergedAt: "2026-11-10T12:30:00Z"));

        // Act
        await CheckAsync();

        // Assert
        _http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_read_sends_the_etag_and_takes_the_cached_answer()
    {
        // Arrange
        Answer("pulls/7", Pull(), etag: "\"pr-1\"");
        Answer($"commits/{Head}/check-runs?per_page=100", Runs(Run(1, "completed", "success")), etag: "\"runs-1\"");
        await CheckAsync();

        // Act
        var result = await CheckAsync();

        // Assert
        result.Should().BeOfType<PullRequestCheckResult.Checked>().Which.Status.Ci.Should().Be(CiState.Passed);
    }

    [Fact]
    public async Task A_second_read_is_conditional()
    {
        // Arrange
        Answer("pulls/7", Pull(), etag: "\"pr-1\"");
        Answer($"commits/{Head}/check-runs?per_page=100", Runs(Run(1, "completed", "success")), etag: "\"runs-1\"");
        await CheckAsync();

        // Act
        await CheckAsync();

        // Assert
        _http.Requests[2].Header("If-None-Match").Should().Be("\"pr-1\"");
    }

    [Fact]
    public async Task A_failed_runs_annotations_are_read_once()
    {
        // Arrange
        Answer("pulls/7", Pull());
        Answer($"commits/{Head}/check-runs?per_page=100", Runs(Run(42, "completed", "failure", 2)));
        Answer("check-runs/42/annotations?per_page=50&page=1", Annotations);
        await CheckAsync();

        // Act
        await CheckAsync();

        // Assert
        _http.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/annotations", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Signed_out_the_status_is_read_without_a_token()
    {
        // Arrange
        Answer("pulls/7", Pull("closed"));

        // Act
        await CheckAsync(token: null);

        // Assert
        _http.Requests[0].Header("Authorization").Should().BeNull();
    }

    [Fact]
    public async Task A_used_up_rate_limit_fails_with_its_reset()
    {
        // Arrange
        _http.On(HttpMethod.Get, Api + "pulls/7", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1794321000");
            return response;
        });

        // Act
        var result = await CheckAsync();

        // Assert
        result.Should().BeOfType<PullRequestCheckResult.Failed>().Which.Error.RetryAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1794321000));
    }

    [Fact]
    public async Task A_missing_pull_request_fails_as_not_found()
    {
        // Arrange
        _http.OnGet(Api + "pulls/7", """{"message":"Not Found"}""", HttpStatusCode.NotFound);

        // Act
        var result = await CheckAsync();

        // Assert
        result.Should().BeOfType<PullRequestCheckResult.Failed>().Which.Error.Problem.Should().Be(PublishProblem.NotFound);
    }

    // ---------- Polling ----------

    private static readonly DateTimeOffset Now = PublishBed.Now;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void Failed_reads_back_off_up_to_half_an_hour(int errors, int minutes)
    {
        // Act
        var next = PullRequestPolling.Next(new PullRequestPoll(PullRequestState.Open, CiState.Pending, Now, errors), Now);

        // Assert
        next.Should().Be(Now.AddMinutes(minutes));
    }

    [Fact]
    public void A_rate_limit_waits_for_its_reset()
    {
        // Act
        var next = PullRequestPolling.Next(new PullRequestPoll(PullRequestState.Open, CiState.Pending, Now, 1, Now.AddMinutes(45)), Now);

        // Assert
        next.Should().Be(Now.AddMinutes(45));
    }

    [Theory]
    [InlineData(PullRequestState.Merged)]
    [InlineData(PullRequestState.Closed)]
    public void A_merged_or_closed_pull_request_is_not_read_again(PullRequestState state)
    {
        // Act
        var next = PullRequestPolling.Next(new PullRequestPoll(state, CiState.Passed, Now), Now);

        // Assert
        next.Should().BeNull();
    }

    [Fact]
    public void A_rejected_pull_request_is_read_every_ten_minutes()
    {
        // Act
        var next = PullRequestPolling.Next(new PullRequestPoll(PullRequestState.Open, CiState.Failed, Now.AddDays(-1)), Now);

        // Assert
        next.Should().Be(Now.AddMinutes(10));
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(30, 5)]
    [InlineData(180, 30)]
    public void Running_ci_is_read_often_at_first_then_less(int minutesSinceChange, int minutes)
    {
        // Act
        var next = PullRequestPolling.Next(new PullRequestPoll(PullRequestState.Open, CiState.Pending, Now.AddMinutes(-minutesSinceChange)), Now);

        // Assert
        next.Should().Be(Now.AddMinutes(minutes));
    }
}
