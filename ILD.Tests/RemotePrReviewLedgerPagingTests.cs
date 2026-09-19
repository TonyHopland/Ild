using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
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

        private readonly List<(Func<string, bool> Match, Func<string, HttpResponseMessage> Respond)> _urlRules = new();

        public RoutingHandler Map(Func<string, bool> match, Func<string> body)
        {
            _rules.Add((match, body));
            return this;
        }

        /// <summary>A rule that can see the URL, so a test can answer page 1 and page 2 differently.</summary>
        public RoutingHandler MapUrl(Func<string, bool> match, Func<string, HttpResponseMessage> respond)
        {
            _urlRules.Add((match, respond));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (url.EndsWith("/graphql", StringComparison.Ordinal) && request.Content is not null)
                GraphQlBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            foreach (var (match, respond) in _urlRules)
                if (match(url))
                    return respond(url);

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

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// A page as a real forge serves one: the body plus the <c>Link</c> header
    /// that says whether another follows. Both GitHub and Gitea send it on paged
    /// list routes, and it is the only thing that distinguishes "the end" from
    /// "the page size I was given", so a double that omits it cannot show
    /// whether the walk terminates for the right reason.
    /// </summary>
    private static HttpResponseMessage Page(string body, bool hasNext)
    {
        var resp = Json(body);
        resp.Headers.TryAddWithoutValidation(
            "Link",
            hasNext
                ? "<https://api.github.com/x?page=2>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"last\""
                : "<https://api.github.com/x?page=1>; rel=\"prev\", <https://api.github.com/x?page=1>; rel=\"first\"");
        return resp;
    }

    /// <summary>A page a server capped below what was asked for, whatever the reason.</summary>
    private static string CappedPage(int from, int count)
        => "[" + string.Join(",", Enumerable.Range(from, count).Select(i =>
            "{\"id\":" + (6000000 + i) + ",\"user\":{\"login\":\"tony\"},\"created_at\":\"2026-09-18T19:00:00Z\","
            + "\"body\":\"comment " + i + "\"}")) + "]";

    /// <summary>A full page of pull-request-level comments, as a forge's page one.</summary>
    private static string FullPageOfIssueComments()
        => "[" + string.Join(",", Enumerable.Range(1, 100).Select(i =>
            "{\"id\":" + (5000000 + i) + ",\"user\":{\"login\":\"tony\"},\"created_at\":\"2026-09-18T19:00:00Z\","
            + "\"body\":\"older comment " + i + "\"}")) + "]";

    /// <summary>
    /// The requested page. Split on '&amp;' rather than searched for, because
    /// "page=1" is a substring of "per_page=100" and a fake that matches on the
    /// substring serves page one for ever.
    /// </summary>
    private static int PageOf(string url)
    {
        var value = new Uri(url).Query.TrimStart('?').Split('&')
            .FirstOrDefault(p => p.StartsWith("page=", StringComparison.Ordinal));
        return value is null ? 1 : int.Parse(value["page=".Length..]);
    }

    private static RoutingHandler PagedIssueComments(Func<string, HttpResponseMessage> secondPage) =>
        new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .MapUrl(u => u.Contains("/issues/7/comments", StringComparison.Ordinal),
                u => PageOf(u) == 1 ? Page(FullPageOfIssueComments(), hasNext: true) : secondPage(u))
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

    [Fact]
    public async Task The_newest_comments_on_a_busy_pull_request_are_not_left_on_page_two()
    {
        // A forge serves these oldest first, so past one page the NEWEST — the
        // ones that should fire the edge — are the ones that fall off.
        using var db = new TestDb();
        var handler = PagedIssueComments(_ => Page(
            "[{\"id\":4060000999,\"user\":{\"login\":\"tony\"},\"created_at\":\"2026-09-19T19:00:00Z\","
            + "\"body\":\"the newest thing anybody said\"}]", hasNext: false));

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Equal(101, ledger.Items.Count);
        var newest = ledger.Items.Single(i => i.CommentId == "4060000999");
        Assert.Contains("newest thing", newest.Body, StringComparison.Ordinal);
        Assert.Contains(handler.Urls, u => u.Contains("/issues/7/comments", StringComparison.Ordinal) && PageOf(u) == 2);
        // …and it stopped there rather than walking to the ceiling.
        Assert.DoesNotContain(handler.Urls, u => u.Contains("/issues/7/comments", StringComparison.Ordinal) && PageOf(u) == 3);
    }

    [Fact]
    public async Task A_page_the_forge_refuses_is_a_message_rather_than_a_ledger_missing_its_tail()
    {
        // Swallowed, a failed second page is indistinguishable from "that was
        // the last page" — and a ledger short of its newest items quietly stops
        // firing for them. An unreadable ledger changes no delivery state.
        using var db = new TestDb();
        var handler = PagedIssueComments(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Empty(ledger.Items);
        Assert.False(string.IsNullOrWhiteSpace(ledger.Message));
        Assert.Contains("comments", ledger.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_forge_that_serves_a_smaller_page_than_it_was_asked_for_is_still_read_to_the_end()
    {
        // A server is free to cap what it was handed, so every page can come
        // back shorter than the 100 asked for. Reading "short" as "the end"
        // loses everything past the first page; the Link header is what says
        // whether more exists.
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .MapUrl(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), u => PageOf(u) switch
            {
                1 => Page(CappedPage(1, 50), hasNext: true),
                2 => Page(CappedPage(51, 50), hasNext: true),
                _ => Page(CappedPage(101, 7), hasNext: false),
            })
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Equal(107, ledger.Items.Count);
        Assert.Contains(ledger.Items, i => i.CommentId == "6000107");
    }

    [Fact]
    public async Task A_forge_without_the_routes_the_ledger_is_read_from_says_so_without_asking()
    {
        // Checked against the live Forgejo 9.0.3 (Gitea 1.22.0 API): there is no
        // pulls/{index}/comments collection and no reply route at all — review
        // comments hang off pulls/{index}/reviews/{id}/comments. Inheriting the
        // GitHub read there would 404 every tick and report an outage for
        // something simply not implemented.
        using var db = new TestDb();
        var handler = new RoutingHandler();

        var service = CreateService(db, handler, "Forgejo", "https://gitea.example");
        var ledger = await service.GetPullRequestReviewLedgerAsync("https://gitea.example/team/repo.git", "5");

        Assert.Empty(ledger.Items);
        Assert.Contains("not supported", ledger.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Forgejo", ledger.Message, StringComparison.Ordinal);
        var reply = await service.ReplyToReviewThreadAsync("https://gitea.example/team/repo.git", "5", "1", "hi");
        Assert.False(reply.Ok);
        Assert.Contains("not supported", reply.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task A_page_that_fills_what_was_asked_for_without_a_link_header_is_not_assumed_to_be_the_last()
    {
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .MapUrl(u => u.Contains("/issues/7/comments", StringComparison.Ordinal),
                u => PageOf(u) == 1 ? Json(FullPageOfIssueComments()) : Json("[]"))
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Equal(100, ledger.Items.Count);
        Assert.Contains(handler.Urls, u => u.Contains("/issues/7/comments", StringComparison.Ordinal) && PageOf(u) == 2);
    }

    [Fact]
    public async Task A_comment_too_long_to_keep_whole_is_still_known_to_be_ilds()
    {
        // The PR node's own answer is a rendered {{PreviousNode.Output}}, easily
        // past the body cap, and the marker sits at the end — so the stored body
        // has had it cut off. Deciding from that body alone, the loop would read
        // its own answer as a reviewer's and start another round.
        using var db = new TestDb();
        var longAnswer = PrCommentMarker.Stamp(new string('x', 4000), Guid.NewGuid());
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal),
                () => "[{\"id\":4053396920,\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-18T19:00:00Z\","
                    + "\"body\":" + System.Text.Json.JsonSerializer.Serialize(longAnswer) + "}]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var ours = Assert.Single(ledger.Items);
        Assert.False(PrCommentMarker.IsStamped(ours.Body), "the body must be truncated past the marker for this to be the real case");
        Assert.True(ours.PostedByIld);

        // …and the throttle must reach the same conclusion from the item alone.
        var watching = PrCommentDelivery.Decide(
            new RemotePrReviewLedger(Array.Empty<RemotePrReviewSummary>(), Array.Empty<RemotePrReviewItem>(), Head, null),
            Head, null).Ledger;
        Assert.Empty(PrCommentDelivery.Decide(ledger, Head, watching).Items);
    }

    [Fact]
    public async Task A_list_that_fits_on_one_page_is_still_one_request()
    {
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal),
                () => "[{\"id\":1,\"user\":{\"login\":\"tony\"},\"created_at\":\"2026-09-18T19:00:00Z\",\"body\":\"ping\"}]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Single(handler.Urls, u => u.Contains("/issues/7/comments", StringComparison.Ordinal));
    }

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
    public async Task With_no_threads_reported_a_reply_chain_still_shares_one_thread_id()
    {
        // GraphQL can answer with no threads at all — a stripped response, a
        // token without the scope — and then the root of the reply chain is the
        // only thread identity there is. The repeat suppression needs items in
        // one conversation to agree on it.
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () =>
                "[{\"id\":1,\"path\":\"src/A.cs\",\"line\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"root\"},"
                + "{\"id\":2,\"in_reply_to_id\":1,\"path\":\"src/A.cs\",\"line\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"bob\"},\"created_at\":\"2026-09-18T18:00:00Z\",\"body\":\"reply\"},"
                + "{\"id\":3,\"in_reply_to_id\":2,\"path\":\"src/A.cs\",\"line\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T18:10:00Z\",\"body\":\"reply to the reply\"}]")
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), () => "{\"data\":null}")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

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
        Assert.Contains("not supported", ledger.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AzureDevOps", ledger.Message, StringComparison.Ordinal);
        Assert.False((await service.ReplyToReviewThreadAsync(repo, "7", "1", "hi")).Ok);
        Assert.False((await service.ResolveReviewThreadAsync(repo, "7", "t1")).Ok);
        Assert.Empty(handler.Urls);
    }
}
