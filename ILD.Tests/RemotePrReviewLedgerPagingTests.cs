using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The parts of the provider-side ledger that only show up on a pull request
/// bigger than one page, or on a provider with no thread concept at all: the
/// GraphQL cursor walk, the fallback that keeps a reply chain together without
/// it, and the marker the adapter reads straight off a comment body.
/// </summary>
public class RemotePrReviewLedgerPagingTests
{
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly List<(Func<string, bool> Match, Func<string> Body)> _rules = new();
        public List<string> GraphQlBodies { get; } = new();
        public List<string> Urls { get; } = new();

        public RoutingHandler Map(Func<string, bool> match, Func<string> body)
        {
            _rules.Add((match, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (url.EndsWith("/graphql", StringComparison.Ordinal) && request.Content is not null)
                GraphQlBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            foreach (var (match, body) in _rules)
            {
                if (match(url))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body(), Encoding.UTF8, "application/json"),
                    };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static RemoteProviderService CreateService(TestDb db, HttpMessageHandler handler, string type, string url)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = type, Type = type, Url = url, ApiKey = "k",
        });
        db.Context.SaveChanges();
        return new RemoteProviderService(
            db.Providers,
            new IRemoteGitProviderAdapter[]
            {
                new ForgejoRemoteGitProviderAdapter(),
                new GitHubRemoteGitProviderAdapter(),
                new AzureDevOpsRemoteGitProviderAdapter(),
            },
            new HttpClient(handler));
    }

    private static string Comments(params string[] ids)
        => "[" + string.Join(",", ids.Select(id =>
            "{\"id\":" + id + ",\"pull_request_review_id\":5250768235,\"path\":\"src/A.cs\",\"line\":10,"
            + "\"original_line\":10,\"original_commit_id\":\"" + Head + "\",\"user\":{\"login\":\"Copilot\"},"
            + "\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"comment " + id + "\"}")) + "]";

    private static string ThreadPage(string threadId, bool hasNext, string? cursor, params string[] commentIds)
        => "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{"
            + "\"pageInfo\":{\"hasNextPage\":" + (hasNext ? "true" : "false") + ",\"endCursor\":"
            + (cursor is null ? "null" : "\"" + cursor + "\"") + "},"
            + "\"nodes\":[{\"id\":\"" + threadId + "\",\"isResolved\":false,\"comments\":{\"nodes\":["
            + string.Join(",", commentIds.Select(id => "{\"fullDatabaseId\":\"" + id + "\"}")) + "]}}]}}}}}";

    [Fact]
    public async Task A_pull_request_with_more_threads_than_one_page_walks_the_cursor()
    {
        using var db = new TestDb();
        var pages = 0;
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => Comments("1", "2"))
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal),
                () => pages++ == 0 ? ThreadPage("PRRT_first", true, "CURSOR_2", "1") : ThreadPage("PRRT_second", false, null, "2"))
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Equal("PRRT_first", ledger.Items.Single(i => i.CommentId == "1").ThreadId);
        Assert.Equal("PRRT_second", ledger.Items.Single(i => i.CommentId == "2").ThreadId);
        Assert.Equal(2, handler.GraphQlBodies.Count);
        Assert.Contains("CURSOR_2", handler.GraphQlBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_page_the_forge_refuses_leaves_the_comments_it_already_has()
    {
        // Half a thread map is still better than no ledger: the comments are
        // what the round acts on, and a missing thread id only costs the reply.
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => Comments("1"))
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var item = Assert.Single(ledger.Items);
        Assert.Equal("1", item.CommentId);
        Assert.False(item.Resolved);
    }

    [Fact]
    public async Task Without_a_thread_api_a_reply_chain_still_shares_one_thread_id()
    {
        // Forgejo has no review-thread resource, so the root of the reply chain
        // is the only thread identity there is — and the repeat suppression
        // needs items in one conversation to agree on it.
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/5/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/5/comments", StringComparison.Ordinal), () =>
                "[{\"id\":1,\"path\":\"src/A.cs\",\"line\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"root\"},"
                + "{\"id\":2,\"in_reply_to_id\":1,\"path\":\"src/A.cs\",\"line\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"bob\"},\"created_at\":\"2026-09-18T18:00:00Z\",\"body\":\"reply\"},"
                + "{\"id\":3,\"in_reply_to_id\":2,\"path\":\"src/A.cs\",\"line\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T18:10:00Z\",\"body\":\"reply to the reply\"}]")
            .Map(u => u.Contains("/issues/5/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/pulls/5", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .GetPullRequestReviewLedgerAsync("https://gitea.example/team/repo.git", "5");

        Assert.Equal(new[] { "1", "1", "1" }, ledger.Items.OrderBy(i => i.CommentId).Select(i => i.ThreadId).ToArray());
    }

    [Fact]
    public async Task A_comment_ild_posted_comes_back_flagged_straight_off_its_body()
    {
        using var db = new TestDb();
        var stamped = System.Text.Json.JsonSerializer.Serialize(
            PrCommentMarker.Stamp("Answered every point.", Guid.NewGuid()));
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () =>
                "[{\"id\":500,\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-18T19:00:00Z\",\"body\":" + stamped + "},"
                + "{\"id\":501,\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-18T19:01:00Z\",\"body\":\"and a person's\"}]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.True(ledger.Items.Single(i => i.CommentId == "500").PostedByIld);
        Assert.False(ledger.Items.Single(i => i.CommentId == "501").PostedByIld);
        // A pull-request-level comment belongs to the head, which is what the
        // per-head repeat suppression keys on.
        Assert.Equal(Head, ledger.Items.Single(i => i.CommentId == "501").Commit);
    }

    [Fact]
    public async Task A_pull_request_the_forge_will_not_serve_is_a_message_not_an_empty_review()
    {
        // An empty ledger read as "nothing on this PR" would seed a first watch
        // that had seen nothing, and the next tick would deliver the lot.
        using var db = new TestDb();
        var handler = new RoutingHandler();

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Empty(ledger.Items);
        Assert.Contains("404", ledger.Message);
    }

    [Fact]
    public async Task Azure_devops_says_it_has_no_ledger_rather_than_asking_for_shapes_it_does_not_have()
    {
        // Left to the base class this would put GitHub-shaped routes on an
        // Azure api base, on every heartbeat tick of every parked run, and
        // report whatever came back as the review.
        const string repo = "https://dev.azure.com/org/project/_git/repo";
        using var db = new TestDb();
        var handler = new RoutingHandler();

        var service = CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org");
        var ledger = await service.GetPullRequestReviewLedgerAsync(repo, "7");

        Assert.Empty(ledger.Items);
        Assert.Contains("Azure DevOps", ledger.Message);
        Assert.False((await service.ReplyToReviewThreadAsync(repo, "7", "1", "hi")).Ok);
        Assert.False((await service.ResolveReviewThreadAsync(repo, "7", "t1")).Ok);
        Assert.Empty(handler.Urls);
    }
}
