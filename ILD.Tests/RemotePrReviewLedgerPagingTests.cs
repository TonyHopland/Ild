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
        public List<(HttpMethod Method, string Url, string Body)> Bodies { get; } = new();

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
            if (request.Content is not null)
            {
                var sent = await request.Content.ReadAsStringAsync(cancellationToken);
                Bodies.Add((request.Method, url, sent));
                if (url.EndsWith("/graphql", StringComparison.Ordinal))
                    GraphQlBodies.Add(sent);
            }

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

    /// <summary>
    /// What GitHub answers for a pull request that simply has no review threads:
    /// the path is there and its <c>nodes</c> are empty. Deliberately not
    /// <c>{"data":null}</c> — that is what an ERROR looks like, and a fake that
    /// used it would be asserting that a broken read passes for a quiet one.
    /// </summary>
    private static string NoThreads()
        => "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{"
            + "\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[]}}}}}";

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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

    /// <summary>A ledger read where only the thread query behaves differently.</summary>
    private static RoutingHandler WithThreads(Func<string> graphql) =>
        new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => Comments("4049159495"))
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), graphql)
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

    private static async Task<RemotePrReviewLedger> ThreadReadAsync(TestDb db, RoutingHandler handler)
        => await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

    [Fact]
    public async Task A_thread_query_the_forge_refuses_is_a_message_rather_than_a_ledger_without_threads()
    {
        // Half a thread list is not a shorter answer, it is a wrong one: every
        // comment whose thread went missing comes back keyed by the root of its
        // reply chain and reported unresolved, so a resolve is aimed at the
        // wrong handle and a settled thread reads as open — under a ledger that
        // says it succeeded.
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => Comments("4049159495"))
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .MapUrl(u => u.EndsWith("/graphql", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await ThreadReadAsync(db, handler);

        Assert.Empty(ledger.Items);
        Assert.Contains("threads", ledger.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_thread_query_that_answers_with_no_thread_data_is_unreadable_too()
    {
        // What a GraphQL error looks like on the wire: HTTP 200, data null.
        // A pull request with no threads answers with the path and no nodes,
        // so a missing path is a failure however healthy the status line is.
        using var db = new TestDb();

        var ledger = await ThreadReadAsync(db, WithThreads(() => "{\"data\":null,\"errors\":[{\"message\":\"Bad credentials\"}]}"));

        Assert.Empty(ledger.Items);
        Assert.Contains("threads", ledger.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_thread_query_that_answers_with_nonsense_is_unreadable_rather_than_fatal()
    {
        using var db = new TestDb();

        var ledger = await ThreadReadAsync(db, WithThreads(() => "not json at all"));

        Assert.Empty(ledger.Items);
        Assert.Contains("threads", ledger.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_thread_walk_with_more_to_come_and_no_cursor_to_ask_with_is_unreadable()
    {
        // There is no way to finish the list from here, so what was read is a
        // prefix of the threads and nothing says so.
        using var db = new TestDb();

        var ledger = await ThreadReadAsync(db, WithThreads(
            () => ThreadPage("PRRT_one", hasNext: true, cursor: null, "4049159495")));

        Assert.Empty(ledger.Items);
        Assert.Contains("threads", ledger.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_thread_walk_still_offered_more_at_its_ceiling_is_unreadable()
    {
        // Twenty pages in and GitHub is still saying there is another. The
        // ceiling is there to stop an unbounded walk, not to make a partial
        // answer look complete.
        using var db = new TestDb();
        var page = 0;

        var ledger = await ThreadReadAsync(db, WithThreads(
            () => ThreadPage("PRRT_" + page++, hasNext: true, cursor: "c" + page, "4049159495")));

        Assert.Empty(ledger.Items);
        Assert.Contains("threads", ledger.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(page >= 20, $"the walk stopped after {page} pages instead of running to its ceiling");
    }

    [Fact]
    public async Task A_pull_request_with_no_threads_at_all_is_still_a_perfectly_good_ledger()
    {
        // The other side of the same rule: empty is an answer, and a provider
        // with nothing to report must not look like one that failed.
        using var db = new TestDb();

        var ledger = await ThreadReadAsync(db, WithThreads(NoThreads));

        Assert.Null(ledger.Message);
        var item = Assert.Single(ledger.Items, i => i.CommentId == "4049159495");
        Assert.False(item.Resolved);
    }

    [Fact]
    public async Task A_list_still_offering_a_next_page_at_the_ceiling_is_unreadable_rather_than_short()
    {
        // Past 2,000 entries the walk stops. Returning what it has would be
        // this feature's founding bug with a bigger number on it: the tail a
        // forge serves last is the NEWEST comments, so the ledger would look
        // valid and never deliver the review that was just posted.
        using var db = new TestDb();
        var handler = PagedIssueComments(_ => Page(FullPageOfIssueComments(), hasNext: true));

        var ledger = await ThreadReadAsync(db, handler);

        Assert.Empty(ledger.Items);
        Assert.Contains("comments", ledger.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, handler.Urls.Count(u => u.Contains("/issues/7/comments", StringComparison.Ordinal)));
    }

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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Equal(107, ledger.Items.Count);
        Assert.Contains(ledger.Items, i => i.CommentId == "6000107");
    }

    [Fact]
    public async Task Forgejo_reads_its_review_comments_from_under_each_review()
    {
        // Checked against the live Forgejo 9.0.3 (Gitea 1.22.0 API): there is no
        // pulls/{index}/comments collection, so the comments hang off each
        // review — one request per review — and a comment carries a resolver
        // once someone has resolved it.
        const string repo = "https://gitea.example/team/repo.git";
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/5/reviews/41/comments", StringComparison.Ordinal), () =>
                "[{\"id\":901,\"path\":\"src/A.cs\",\"position\":10,\"original_position\":10,"
                + "\"original_commit_id\":\"" + Head + "\",\"user\":{\"login\":\"alice\"},"
                + "\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"this allocation is wrong\","
                + "\"resolver\":{\"login\":\"tony\"}}]")
            .Map(u => u.Contains("/pulls/5/reviews", StringComparison.Ordinal), () =>
                "[{\"id\":41,\"state\":\"COMMENT\",\"body\":\"a review\",\"commit_id\":\"" + Head + "\","
                + "\"submitted_at\":\"2026-09-18T17:34:45Z\",\"user\":{\"login\":\"alice\"}}]")
            .Map(u => u.Contains("/issues/5/comments", StringComparison.Ordinal), () =>
                "[{\"id\":7001,\"user\":{\"login\":\"tony\"},\"created_at\":\"2026-09-18T19:00:00Z\",\"body\":\"ping\"}]")
            .Map(u => u.EndsWith("/pulls/5", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var service = CreateService(db, handler, "Forgejo", "https://gitea.example");
        var ledger = await service.GetPullRequestReviewLedgerAsync(repo, "5");

        Assert.Null(ledger.Message);
        Assert.Equal(Head, ledger.HeadSha);
        var inline = ledger.Items.Single(i => i.CommentId == "901");
        Assert.Equal("src/A.cs", inline.Path);
        Assert.Equal(10, inline.Line);
        Assert.Equal("alice", inline.Author);
        Assert.Equal(Head, inline.Commit);
        Assert.True(inline.Resolved);
        Assert.Equal("41", inline.ThreadId);
        var issue = ledger.Items.Single(i => i.CommentId == "7001");
        Assert.Null(issue.Path);
        Assert.Contains("ping", issue.Body, StringComparison.Ordinal);
        Assert.Equal("41", Assert.Single(ledger.Reviews).Id);
    }

    [Fact]
    public async Task Forgejo_answers_on_the_same_file_and_line_because_it_has_no_reply_route()
    {
        const string repo = "https://gitea.example/team/repo.git";
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/5/reviews/41/comments", StringComparison.Ordinal), () =>
                "[{\"id\":901,\"path\":\"src/A.cs\",\"position\":10,\"original_position\":10,"
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"wrong\"}]")
            .MapUrl(u => u.Contains("/pulls/5/reviews", StringComparison.Ordinal) && !u.Contains("/comments", StringComparison.Ordinal),
                _ => Json("[{\"id\":41,\"state\":\"COMMENT\",\"commit_id\":\"" + Head + "\","
                    + "\"submitted_at\":\"2026-09-18T17:34:45Z\",\"user\":{\"login\":\"alice\"}}]"))
            .Map(u => u.Contains("/issues/5/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/pulls/5", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var result = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .ReplyToReviewThreadAsync(repo, "5", "901", "That compiles.");

        Assert.True(result.Ok);
        var posted = Assert.Single(handler.Bodies, b => b.Url.EndsWith("/pulls/5/reviews", StringComparison.Ordinal));
        Assert.Contains("\"path\":\"src/A.cs\"", posted.Body, StringComparison.Ordinal);
        Assert.Contains("That compiles.", posted.Body, StringComparison.Ordinal);
        Assert.Contains("COMMENT", posted.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgejo_cannot_resolve_a_thread_and_says_so_without_asking()
    {
        using var db = new TestDb();
        var handler = new RoutingHandler();

        var result = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .ResolveReviewThreadAsync("https://gitea.example/team/repo.git", "5", "41");

        Assert.False(result.Ok);
        Assert.Contains("not supported", result.Message, StringComparison.OrdinalIgnoreCase);
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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
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
    public async Task A_forge_with_no_thread_endpoint_at_all_is_read_as_unavailable()
    {
        // This used to keep the comments and drop the threads, on the reasoning
        // that half a thread map beats no ledger. It does not: the half that is
        // missing is silent. Every comment whose thread was on it comes back
        // keyed by its reply chain and reported unresolved, so the round resolves
        // the wrong handle and reads a settled thread as open — and the ledger
        // says it succeeded, so nothing anywhere knows to distrust it.
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => Comments("1"))
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Empty(ledger.Items);
        Assert.Contains("threads", ledger.Message, StringComparison.OrdinalIgnoreCase);
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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
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
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), NoThreads)
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

    private const string AzureRepo = "https://dev.azure.com/org/project/_git/repo";

    /// <summary>The threads payload Azure DevOps serves, with the file, line and status already on it.</summary>
    private static string AzureThreads() =>
        "{\"value\":[{\"id\":77,\"status\":\"active\","
        + "\"threadContext\":{\"filePath\":\"/src/A.cs\",\"rightFileStart\":{\"line\":92}},"
        + "\"pullRequestThreadContext\":{\"iterationContext\":{\"secondComparingIteration\":3}},"
        + "\"comments\":[{\"id\":1,\"content\":\"this allocation is wrong\",\"commentType\":\"text\","
        + "\"author\":{\"displayName\":\"Alice\"},\"publishedDate\":\"2026-09-18T17:34:44Z\"},"
        + "{\"id\":2,\"content\":\"updated the source branch\",\"commentType\":\"system\","
        + "\"author\":{\"displayName\":\"Azure\"},\"publishedDate\":\"2026-09-18T17:35:00Z\"}]},"
        + "{\"id\":78,\"status\":\"fixed\",\"comments\":[{\"id\":1,\"content\":\"a note on the pull request\","
        + "\"commentType\":\"text\",\"author\":{\"displayName\":\"Tony\"},\"publishedDate\":\"2026-09-18T19:00:00Z\"}]}]}";

    private static RoutingHandler AzurePr() => new RoutingHandler()
        // The query string is what separates the thread LIST from a route under
        // it; matching the prefix alone would shadow /threads/77/comments.
        .Map(u => u.Contains("/pullrequests/7/threads?", StringComparison.Ordinal), AzureThreads)
        .Map(u => u.Contains("/pullrequests/7?", StringComparison.Ordinal),
            () => "{\"lastMergeSourceCommit\":{\"commitId\":\"" + Head + "\"}}");

    [Fact]
    public async Task Azure_devops_reads_the_whole_review_from_its_threads()
    {
        using var db = new TestDb();
        var handler = AzurePr();

        var ledger = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .GetPullRequestReviewLedgerAsync(AzureRepo, "7");

        Assert.Null(ledger.Message);
        var inline = ledger.Items.Single(i => i.Kind == "review");
        Assert.Equal("77-1", inline.CommentId);
        Assert.Equal("77", inline.ThreadId);
        Assert.Equal("/src/A.cs", inline.Path);
        Assert.Equal(92, inline.Line);
        Assert.Equal("Alice", inline.Author);
        Assert.Equal("3", inline.Commit);
        Assert.False(inline.Resolved);
        // A thread with no file is the pull request's own discussion, and a
        // closed one reports resolved.
        var issue = ledger.Items.Single(i => i.Kind == "issue");
        Assert.Equal("78-1", issue.CommentId);
        Assert.True(issue.Resolved);
        // "updated the source branch" is an event, not review.
        Assert.DoesNotContain(ledger.Items, i => i.Body.Contains("updated the source branch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Azure_devops_answers_on_the_thread_the_comment_sits_in()
    {
        using var db = new TestDb();
        var handler = AzurePr().Map(u => u.Contains("/threads/77/comments", StringComparison.Ordinal), () => "{\"id\":9}");

        var result = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .ReplyToReviewThreadAsync(AzureRepo, "7", "77-1", "That compiles.");

        Assert.True(result.Ok);
        Assert.Equal("77-9", result.Id);
        var posted = Assert.Single(handler.Bodies, b => b.Url.Contains("/threads/77/comments", StringComparison.Ordinal));
        Assert.Contains("\"parentCommentId\":1", posted.Body, StringComparison.Ordinal);
        Assert.Contains("That compiles.", posted.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_devops_resolves_a_thread_by_setting_its_status()
    {
        using var db = new TestDb();
        var handler = AzurePr().MapUrl(
            u => u.Contains("/threads/77?", StringComparison.Ordinal), _ => Json("{\"id\":77,\"status\":\"fixed\"}"));

        var result = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .ResolveReviewThreadAsync(AzureRepo, "7", "77");

        Assert.True(result.Ok);
        var patched = Assert.Single(handler.Bodies, b => b.Method == HttpMethod.Patch);
        Assert.Contains("fixed", patched.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_azure_comment_id_that_is_not_thread_qualified_is_refused_by_name()
    {
        using var db = new TestDb();
        var handler = AzurePr();

        var result = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .ReplyToReviewThreadAsync(AzureRepo, "7", "notathread", "hi");

        Assert.False(result.Ok);
        Assert.Contains("notathread", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Urls, u => u.Contains("/comments", StringComparison.Ordinal));
    }
}
