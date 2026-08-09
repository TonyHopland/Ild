using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

public class RemoteProviderServiceTests
{
    private static RemoteProviderService CreateService(TestDb db, HttpMessageHandler handler)
        => new(
            db.Providers,
            new IRemoteGitProviderAdapter[]
            {
                new ForgejoRemoteGitProviderAdapter(),
                new GitHubRemoteGitProviderAdapter(),
            },
            new HttpClient(handler));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Clone(request));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"url\":\"https://example.test/api/pr/1\",\"html_url\":\"https://example.test/pr/1\"}", Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(response);
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    // Routes GitHub REST calls to canned JSON by URL, in declared order so more
    // specific paths (…/pulls/7/reviews) match before …/pulls/7.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly List<(Func<string, bool> Match, Func<string> Body)> _rules = new();
        public int PrDetailCalls { get; private set; }

        public RoutingHandler Map(Func<string, bool> match, Func<string> body)
        {
            _rules.Add((match, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/pulls/7", StringComparison.Ordinal)) PrDetailCalls++;
            foreach (var (match, body) in _rules)
            {
                if (match(url))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body(), Encoding.UTF8, "application/json"),
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static void AddGitHub(TestDb db)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "github",
            Type = "GitHub",
            Url = "https://github.com",
            ApiKey = "k",
        });
        db.Context.SaveChanges();
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_aggregates_pr_reviews_ci_and_conversation()
    {
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews"), () =>
                "[{\"user\":{\"login\":\"alice\"},\"state\":\"APPROVED\",\"body\":\"lgtm\",\"submitted_at\":\"2026-01-01T00:00:00Z\"}]")
            .Map(u => u.Contains("/pulls/7/comments"), () =>
                "[{\"user\":{\"login\":\"carol\"},\"body\":\"inline\",\"created_at\":\"2026-01-03T00:00:00Z\"}]")
            .Map(u => u.Contains("/issues/7/comments"), () =>
                "[{\"user\":{\"login\":\"bob\"},\"body\":\"hi\",\"created_at\":\"2026-01-02T00:00:00Z\"}]")
            .Map(u => u.Contains("/check-runs"), () =>
                "{\"check_runs\":[{\"status\":\"completed\",\"conclusion\":\"failure\"}]}")
            .Map(u => u.Contains("/commits/abc/status"), () => "{\"state\":\"success\",\"statuses\":[]}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"title\":\"My PR\",\"body\":\"desc\",\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"mergeable_state\":\"clean\",\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.NotNull(snapshot);
        Assert.Equal("My PR", snapshot!.Title);
        Assert.Equal("open", snapshot.State);
        Assert.False(snapshot.Merged);
        Assert.True(snapshot.Mergeable);
        Assert.True(snapshot.Approved);
        Assert.False(snapshot.ChangesRequested);
        Assert.Equal(RemotePrCiStatus.Failed, snapshot.Ci);
        // review (Jan 1) < issue comment (Jan 2) < review comment (Jan 3).
        Assert.Equal(3, snapshot.Conversation.Count);
        Assert.Equal("review", snapshot.Conversation[0].Kind);
        Assert.Equal("comment", snapshot.Conversation[1].Kind);
        Assert.Equal("review_comment", snapshot.Conversation[2].Kind);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_keeps_approval_when_a_later_comment_review_follows()
    {
        // A reviewer APPROVES, then later posts a COMMENTED review. GitHub keeps
        // the approval as the reviewer's decision; a comment must not drop it.
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews"), () =>
                "[{\"user\":{\"login\":\"alice\"},\"state\":\"APPROVED\",\"body\":\"lgtm\",\"submitted_at\":\"2026-01-01T00:00:00Z\"},"
                + "{\"user\":{\"login\":\"alice\"},\"state\":\"COMMENTED\",\"body\":\"nit\",\"submitted_at\":\"2026-01-02T00:00:00Z\"}]")
            .Map(u => u.Contains("/comments") || u.Contains("/check-runs") || u.Contains("/status"), () => "[]")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Approved);
        Assert.False(snapshot.ChangesRequested);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_drops_approval_once_dismissed()
    {
        // A reviewer APPROVES, then that review is DISMISSED: approval no longer counts.
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews"), () =>
                "[{\"user\":{\"login\":\"alice\"},\"state\":\"APPROVED\",\"body\":\"\",\"submitted_at\":\"2026-01-01T00:00:00Z\"},"
                + "{\"user\":{\"login\":\"alice\"},\"state\":\"DISMISSED\",\"body\":\"\",\"submitted_at\":\"2026-01-02T00:00:00Z\"}]")
            .Map(u => u.Contains("/comments") || u.Contains("/check-runs") || u.Contains("/status"), () => "[]")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Approved);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_retries_while_mergeable_unknown()
    {
        using var db = new TestDb();
        AddGitHub(db);

        var prCalls = 0;
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/reviews") || u.Contains("/comments") || u.Contains("/check-runs") || u.Contains("/status"),
                () => "[]")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
            {
                prCalls++;
                // First fetch: GitHub still computing mergeability → null.
                return prCalls == 1
                    ? "{\"state\":\"open\",\"merged\":false,\"mergeable\":null,\"head\":{\"sha\":\"abc\"}}"
                    : "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}";
            });

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Mergeable);
        Assert.True(prCalls >= 2, $"expected a retry while mergeable was unknown; PR fetched {prCalls} time(s)");
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_keeps_the_failing_checks_behind_a_red_verdict()
    {
        // The Failed enum alone leaves a fix-it loop guessing. Everything kept
        // here comes out of the two CI responses already fetched — no extra
        // round-trip, and no job-log fetch: details_url is the link to the rest.
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/reviews") || u.Contains("/comments"), () => "[]")
            .Map(u => u.Contains("/check-runs"), () =>
                "{\"check_runs\":["
                + "{\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"failure\",\"details_url\":\"https://ci/build\","
                + "\"output\":{\"title\":\"Build failed\",\"summary\":\"tsc: 3 errors\",\"text\":\"src/a.ts(4,1): TS2345\"}},"
                + "{\"name\":\"lint\",\"status\":\"completed\",\"conclusion\":\"success\"}]}")
            .Map(u => u.Contains("/commits/abc/status"), () =>
                "{\"state\":\"failure\",\"statuses\":[{\"context\":\"coverage\",\"state\":\"failure\","
                + "\"description\":\"dropped 4%\",\"target_url\":\"https://ci/coverage\"}]}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.NotNull(snapshot);
        Assert.Equal(RemotePrCiStatus.Failed, snapshot!.Ci);

        // Only the failing ones — the green check run is not detail about a failure.
        Assert.Equal(2, snapshot.FailedChecks.Count);
        var build = snapshot.FailedChecks[0];
        Assert.Equal("build", build.Name);
        Assert.Equal("failure", build.Conclusion);
        Assert.Equal("https://ci/build", build.Url);
        Assert.Contains("tsc: 3 errors", build.Summary);
        Assert.Contains("TS2345", build.Summary);

        var coverage = snapshot.FailedChecks[1];
        Assert.Equal("coverage", coverage.Name);
        Assert.Equal("https://ci/coverage", coverage.Url);
        Assert.Equal("dropped 4%", coverage.Summary);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_takes_the_job_id_from_details_url_as_the_log_handle()
    {
        // get_ci_log keys on the Actions *job* id, which is not the check-run id
        // and appears only in details_url. Capturing the wrong number would give
        // the agent a handle that always answers "no log".
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/reviews") || u.Contains("/comments") || u.Contains("/status"), () => "[]")
            .Map(u => u.Contains("/check-runs"), () =>
                "{\"check_runs\":["
                + "{\"id\":111,\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"failure\","
                + "\"details_url\":\"https://github.com/team/repo/actions/runs/500/job/67890\"},"
                + "{\"id\":222,\"name\":\"scan\",\"status\":\"completed\",\"conclusion\":\"failure\","
                + "\"details_url\":\"https://scanner.example/report/9\"}]}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.Equal("67890", snapshot!.FailedChecks[0].CheckId);
        // A third-party check run has no job: its own id is the only handle to
        // offer, and the log fetch will say there is nothing behind it.
        Assert.Equal("222", snapshot.FailedChecks[1].CheckId);
    }

    [Fact]
    public async Task GetCheckLogAsync_returns_the_tail_of_a_github_job_log()
    {
        using var db = new TestDb();
        AddGitHub(db);

        var log = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/actions/jobs/67890/logs"), () => log);

        var window = await CreateService(db, handler)
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 3, offset: 0);

        Assert.True(window.Available);
        Assert.Equal("line 498\nline 499\nline 500", window.Text);
        Assert.Equal(3, window.Lines);
        Assert.Equal(500, window.TotalLines);
        Assert.False(window.Truncated);
    }

    [Fact]
    public async Task GetCheckLogAsync_walks_backwards_with_offset()
    {
        using var db = new TestDb();
        AddGitHub(db);

        var log = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/actions/jobs/67890/logs"), () => log);

        var window = await CreateService(db, handler)
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 2, offset: 3);

        Assert.Equal("line 496\nline 497", window.Text);
        Assert.Equal(3, window.Offset);
    }

    [Fact]
    public async Task GetCheckLogAsync_caps_one_response_and_says_it_truncated()
    {
        // A CI log runs to megabytes; one call returns a readable window and
        // tells the agent there is more, so it pages instead of assuming it saw
        // the whole thing.
        using var db = new TestDb();
        AddGitHub(db);

        var log = string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i} {new string('x', 200)}"));
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/actions/jobs/67890/logs"), () => log);

        var window = await CreateService(db, handler)
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 2000, offset: 0);

        Assert.True(window.Truncated);
        Assert.True(window.Text!.Length <= 16_000, $"response was {window.Text.Length} chars");
        // The cap keeps the END of the window — that is where the error is.
        Assert.EndsWith("line 5000 " + new string('x', 200), window.Text);
        Assert.Equal(5000, window.TotalLines);
    }

    [Fact]
    public async Task GetCheckLogAsync_holds_only_the_window_of_a_huge_log_not_the_log()
    {
        // A CI log runs to tens of megabytes. Reducing it to a 16k window must
        // not cost several times its size in strings first, so the reader
        // streams and keeps only the lines the window could still need.
        using var db = new TestDb();
        AddGitHub(db);

        // ~24 MB of log, streamed from a generator so the test itself does not
        // hold it either — anything the reader retains shows up as growth here.
        var handler = new StreamingLogHandler(lineCount: 200_000, lineLength: 120);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);

        var window = await CreateService(db, handler)
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 10, offset: 0);

        var retained = GC.GetTotalMemory(true) - before;

        Assert.True(window.Available);
        Assert.Equal(200_000, window.TotalLines);
        Assert.Equal(10, window.Lines);
        Assert.EndsWith("line 200000", window.Text!.TrimEnd());
        // Buffering the body would leave tens of MB live at this point.
        Assert.True(retained < 8_000_000, $"reading the log retained {retained / 1_000_000.0:F1} MB");
    }

    [Fact]
    public async Task GetCheckLogAsync_says_so_when_the_offset_is_past_the_start_of_the_log()
    {
        using var db = new TestDb();
        AddGitHub(db);

        var log = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var handler = new RoutingHandler().Map(u => u.Contains("/actions/jobs/67890/logs"), () => log);

        var window = await CreateService(db, handler)
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 5, offset: 500);

        Assert.Equal(string.Empty, window.Text);
        Assert.True(window.Truncated);
        Assert.Contains("20 lines", window.Message);
    }

    [Fact]
    public async Task GetCheckLogAsync_keeps_one_absurd_line_from_filling_the_window()
    {
        // Minified bundles and base64 blobs arrive as a single line; one of them
        // must not crowd out the lines around the actual error.
        using var db = new TestDb();
        AddGitHub(db);

        var log = "before\n" + new string('x', 500_000) + "\nafter";
        var handler = new RoutingHandler().Map(u => u.Contains("/actions/jobs/67890/logs"), () => log);

        var window = await CreateService(db, handler)
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 3, offset: 0);

        Assert.Equal(3, window.TotalLines);
        Assert.StartsWith("before", window.Text);
        Assert.EndsWith("after", window.Text);
        Assert.True(window.Text!.Length < 20_000, $"one line contributed {window.Text.Length} chars");
    }

    [Fact]
    public async Task GetCheckLogAsync_reports_a_transport_failure_without_echoing_its_internals()
    {
        // The message reaches an agent. What it can act on is "the request
        // failed"; hosts and URLs from an exception belong in the server log.
        using var db = new TestDb();
        AddGitHub(db);

        var window = await CreateService(db, new ThrowingHandler("connect to 10.1.2.3:443 failed"))
            .GetCheckLogAsync("https://github.com/team/repo", "67890", tailLines: 10, offset: 0);

        Assert.False(window.Available);
        Assert.DoesNotContain("10.1.2.3", window.Message);
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(message);
    }

    /// <summary>Serves a large log from a generator, so the fixture never holds it either.</summary>
    private sealed class StreamingLogHandler(int lineCount, int lineLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var padding = new string('.', Math.Max(0, lineLength - 20));
            IEnumerable<byte> Body()
            {
                for (var i = 1; i <= lineCount; i++)
                    foreach (var b in Encoding.UTF8.GetBytes($"{padding} line {i}\n"))
                        yield return b;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EnumerableStream(Body())),
            });
        }
    }

    /// <summary>A forward-only stream over a lazily generated byte sequence.</summary>
    private sealed class EnumerableStream(IEnumerable<byte> bytes) : Stream
    {
        private readonly IEnumerator<byte> _bytes = bytes.GetEnumerator();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && _bytes.MoveNext())
                buffer[offset + read++] = _bytes.Current;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task GetCheckLogAsync_reports_an_expired_or_foreign_check_as_unavailable_not_an_error()
    {
        using var db = new TestDb();
        AddGitHub(db);

        // RoutingHandler 404s anything unmapped — GitHub's answer for a job whose
        // logs have aged out, or a check run that never was an Actions job.
        var window = await CreateService(db, new RoutingHandler())
            .GetCheckLogAsync("https://github.com/team/repo", "222", tailLines: 100, offset: 0);

        Assert.False(window.Available);
        Assert.Null(window.Text);
        Assert.NotNull(window.Message);
    }

    [Fact]
    public async Task GetCheckLogAsync_on_forgejo_says_its_ci_lives_elsewhere()
    {
        // Decided with the human: Forgejo commit statuses come from an external
        // CI this server may hold no credentials for, so the tool answers rather
        // than errors, and the caller falls back to the URL.
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "forgejo",
            Type = "Forgejo",
            Url = "https://forge.example",
            ApiKey = "k",
        });
        db.Context.SaveChanges();

        var window = await CreateService(db, new RoutingHandler())
            .GetCheckLogAsync("https://forge.example/team/repo", "44", tailLines: 100, offset: 0);

        Assert.False(window.Available);
        Assert.Contains("Forgejo", window.Message);
    }

    [Fact]
    public async Task GetCheckLogAsync_without_a_configured_provider_is_an_answer_not_a_throw()
    {
        using var db = new TestDb();
        var window = await CreateService(db, new RoutingHandler())
            .GetCheckLogAsync("https://unknown.example/team/repo", "1", tailLines: 100, offset: 0);

        Assert.False(window.Available);
        Assert.NotNull(window.Message);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_captures_forgejo_commit_status_detail()
    {
        // Forgejo/Gitea has no check-runs endpoint, so commit statuses are its
        // only CI signal — and it names an individual context's verdict
        // "status", keeping "state" for the combined rollup. Reading only
        // GitHub's spelling left this provider with the verdict and no detail
        // behind it (ADR-0009: the two must behave alike).
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "forgejo",
            Type = "Forgejo",
            Url = "https://forge.example",
            ApiKey = "k",
        });
        db.Context.SaveChanges();

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/reviews") || u.Contains("/comments") || u.Contains("/check-runs"), () => "[]")
            .Map(u => u.Contains("/commits/abc/status"), () =>
                "{\"state\":\"failure\",\"statuses\":["
                + "{\"context\":\"ci/woodpecker\",\"status\":\"failure\",\"description\":\"pipeline failed\","
                + "\"target_url\":\"https://forge.example/ci/9\"},"
                + "{\"context\":\"ci/lint\",\"status\":\"success\"}]}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://forge.example/team/repo", "7");

        Assert.Equal(RemotePrCiStatus.Failed, snapshot!.Ci);
        var check = Assert.Single(snapshot.FailedChecks);
        Assert.Equal("ci/woodpecker", check.Name);
        Assert.Equal("failure", check.Conclusion);
        Assert.Equal("https://forge.example/ci/9", check.Url);
        Assert.Equal("pipeline failed", check.Summary);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_reports_a_red_rollup_with_no_failing_context()
    {
        // A provider that publishes only the aggregate state still has to route
        // on_ci_failed, and still has to say something rather than nothing.
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/reviews") || u.Contains("/comments") || u.Contains("/check-runs"), () => "[]")
            .Map(u => u.Contains("/commits/abc/status"), () =>
                "{\"state\":\"error\",\"statuses\":[{\"context\":\"ci\",\"state\":\"pending\"}]}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.Equal(RemotePrCiStatus.Failed, snapshot!.Ci);
        Assert.Equal("error", Assert.Single(snapshot.FailedChecks).Conclusion);
    }

    [Fact]
    public async Task GetPullRequestSnapshotAsync_truncates_a_huge_check_output()
    {
        // CI output has no upper bound and the snapshot is persisted on every
        // heartbeat tick, so one check cannot contribute unbounded text.
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new RoutingHandler()
            .Map(u => u.Contains("/reviews") || u.Contains("/comments") || u.Contains("/status"), () => "[]")
            .Map(u => u.Contains("/check-runs"), () =>
                "{\"check_runs\":[{\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"failure\","
                + "\"output\":{\"summary\":\"" + new string('x', 50_000) + "\"}}]}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () =>
                "{\"state\":\"open\",\"merged\":false,\"mergeable\":true,\"head\":{\"sha\":\"abc\"}}");

        var snapshot = await CreateService(db, handler)
            .GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        var summary = Assert.Single(snapshot!.FailedChecks).Summary;
        Assert.True(summary!.Length < 1100, $"check output kept unbounded ({summary.Length} chars)");
    }

    [Fact]
    public async Task CreatePullRequestAsync_sends_provider_api_key_in_auth_header()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "gitea",
            Type = "Forgejo",
            Url = "https://gitea.example",
            ApiKey = "provider-key",
        });
        db.Context.SaveChanges();

        var handler = new RecordingHandler();
        var service = CreateService(db, handler);

        var result = await service.CreatePullRequestAsync(
            "https://gitea.example/team/repo.git",
            "ild/test",
            "main",
            "title",
            "body");

        Assert.Null(result.Error);
        Assert.Single(handler.Requests);
        Assert.Equal(new AuthenticationHeaderValue("token", "provider-key"), handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task CreatePullRequestAsync_uses_matching_provider_api_key_for_each_request()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.AddRange(
            new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "gitea",
                Type = "Forgejo",
                Url = "https://gitea.example",
                ApiKey = "gitea-key",
            },
            new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "forgejo",
                Type = "Forgejo",
                Url = "https://forge.example",
                ApiKey = "forge-key",
            });
        db.Context.SaveChanges();

        var handler = new RecordingHandler();
        var service = CreateService(db, handler);

        await service.CreatePullRequestAsync("https://gitea.example/team/repo.git", "ild/one", "main", "title", "body");
        await service.CreatePullRequestAsync("https://forge.example/team/repo.git", "ild/two", "main", "title", "body");

        Assert.Equal(2, handler.Requests.Count());
        Assert.Equal(new AuthenticationHeaderValue("token", "gitea-key"), handler.Requests[0].Headers.Authorization);
        Assert.Equal(new AuthenticationHeaderValue("token", "forge-key"), handler.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task CreatePullRequestAsync_uses_github_api_base_and_headers()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "github",
            Type = "GitHub",
            Url = "https://github.com",
            ApiKey = "github-key",
        });
        db.Context.SaveChanges();

        var handler = new RecordingHandler();
        var service = CreateService(db, handler);

        var result = await service.CreatePullRequestAsync(
            "https://github.com/team/repo.git",
            "ild/test",
            "main",
            "title",
            "body");

        Assert.Null(result.Error);
        Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/team/repo/pulls", handler.Requests[0].RequestUri?.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "github-key"), handler.Requests[0].Headers.Authorization);
        Assert.Contains(handler.Requests[0].Headers.Accept, h => h.MediaType == "application/vnd.github+json");
        Assert.Contains(handler.Requests[0].Headers.UserAgent, h => h.Product?.Name == "ILD");
    }

    // Records each request's method, URL and body, returning a caller-supplied
    // response per request — used to inspect the multi-call auto-merge flows.
    private sealed class AutoMergeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = new();

        public AutoMergeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            Calls.Add((request.Method, request.RequestUri!.ToString(), body));
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task EnablePullRequestAutoMergeAsync_forgejo_schedules_merge_when_checks_succeed()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "gitea",
            Type = "Forgejo",
            Url = "https://gitea.example",
            ApiKey = "provider-key",
        });
        db.Context.SaveChanges();

        var handler = new AutoMergeHandler(_ => Json("{}"));
        var service = CreateService(db, handler);

        var enabled = await service.EnablePullRequestAutoMergeAsync("https://gitea.example/team/repo.git", "5");

        Assert.True(enabled);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://gitea.example/api/v1/repos/team/repo/pulls/5/merge", call.Url);
        Assert.Contains("merge_when_checks_succeed", call.Body);
    }

    [Fact]
    public async Task EnablePullRequestAutoMergeAsync_github_enables_via_graphql_using_node_id()
    {
        using var db = new TestDb();
        AddGitHub(db);

        var handler = new AutoMergeHandler(req => req.Method == HttpMethod.Get
            ? Json("{\"node_id\":\"PR_node_1\"}")
            : Json("{\"data\":{\"enablePullRequestAutoMerge\":{\"clientMutationId\":null}}}"));
        var service = CreateService(db, handler);

        var enabled = await service.EnablePullRequestAutoMergeAsync("https://github.com/team/repo.git", "11");

        Assert.True(enabled);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Method);
        Assert.Equal("https://api.github.com/repos/team/repo/pulls/11", handler.Calls[0].Url);
        Assert.Equal("https://api.github.com/graphql", handler.Calls[1].Url);
        Assert.Contains("enablePullRequestAutoMerge", handler.Calls[1].Body);
        Assert.Contains("PR_node_1", handler.Calls[1].Body);
    }

    [Fact]
    public async Task EnablePullRequestAutoMergeAsync_github_returns_false_when_repository_disallows_it()
    {
        using var db = new TestDb();
        AddGitHub(db);

        // GitHub answers the mutation with HTTP 200 + an errors array when the
        // repository has auto-merge disabled.
        var handler = new AutoMergeHandler(req => req.Method == HttpMethod.Get
            ? Json("{\"node_id\":\"PR_node_1\"}")
            : Json("{\"errors\":[{\"message\":\"Auto-merge is not allowed for this repository\"}]}"));
        var service = CreateService(db, handler);

        var enabled = await service.EnablePullRequestAutoMergeAsync("https://github.com/team/repo.git", "11");

        Assert.False(enabled);
    }

    [Fact]
    public async Task CreatePullRequestAsync_matches_github_repo_urls_when_provider_uses_api_host()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "github",
            Type = "GitHub",
            Url = "https://api.github.com",
            ApiKey = "github-key",
        });
        db.Context.SaveChanges();

        var handler = new RecordingHandler();
        var service = CreateService(db, handler);

        var result = await service.CreatePullRequestAsync(
            "https://github.com/team/repo.git",
            "ild/test",
            "main",
            "title",
            "body");

        Assert.Null(result.Error);
        Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/team/repo/pulls", handler.Requests[0].RequestUri?.ToString());
    }
}