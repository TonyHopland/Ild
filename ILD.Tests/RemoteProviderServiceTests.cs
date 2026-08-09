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