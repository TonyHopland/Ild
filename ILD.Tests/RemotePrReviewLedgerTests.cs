using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The provider side of the review ledger. The shapes here are the ones
/// api.github.com actually returned for PR #158 (checked against the live API
/// and the published GraphQL schema), because the whole point of the ledger is
/// to see what the rendered review hides — a double that guesses would hide it
/// all over again.
/// </summary>
public class RemotePrReviewLedgerTests
{
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";
    private const string LaterCommit = "acf809aeb1cffe17c62735b2174ed42b00ab3471";

    private static RemoteProviderService CreateService(TestDb db, HttpMessageHandler handler)
        => new(
            db.Providers,
            new IRemoteGitProviderAdapter[]
            {
                new ForgejoRemoteGitProviderAdapter(),
                new GitHubRemoteGitProviderAdapter(),
                new AzureDevOpsRemoteGitProviderAdapter(),
            },
            new HttpClient(handler));

    private static void AddProvider(TestDb db, string type, string url)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = type,
            Type = type,
            Url = url,
            ApiKey = "k",
        });
        db.Context.SaveChanges();
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, Func<string> Body)> _rules = new();
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = new();

        public RoutingHandler Map(Func<string, bool> match, Func<string> body)
        {
            _rules.Add((r => match(r.RequestUri!.ToString()), body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.ToString(), body));
            foreach (var (match, responseBody) in _rules)
            {
                if (match(request))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseBody(), Encoding.UTF8, "application/json"),
                    };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection reset");
    }

    private const string ReviewBody =
        "### Changes recommended\n\n<details>\n<summary>Review details</summary>\n\n### Suppressed comments (1)\n\n"
        + "**frontend/src/utils/attachments.ts:30**\n* `attachedNote` trims what the human typed; preserve it.\n"
        + "```\n  const text = typed.trim();\n```\n\n- **Files reviewed:** 25/25 changed files\n</details>";

    private static string Reviews() =>
        "[{\"id\":5250768235,\"user\":{\"login\":\"copilot-pull-request-reviewer[bot]\"},\"state\":\"COMMENTED\","
        + "\"submitted_at\":\"2026-09-18T17:34:45Z\",\"commit_id\":\"" + Head + "\",\"body\":"
        + System.Text.Json.JsonSerializer.Serialize(ReviewBody) + "}]";

    // id, pull_request_review_id, original_commit_id and in_reply_to_id are the
    // fields the ledger is built from; commit_id drifts as the branch moves and
    // is deliberately different here.
    private static string ReviewComments() =>
        "[{\"id\":4049159495,\"pull_request_review_id\":5250768235,\"path\":\"frontend/src/components/workitem-v2/WorkItemModalV2.tsx\","
        + "\"line\":92,\"original_line\":92,\"commit_id\":\"" + LaterCommit + "\",\"original_commit_id\":\"" + Head + "\","
        + "\"user\":{\"login\":\"Copilot\"},\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"requestClose can still confirm\"},"
        + "{\"id\":4051372317,\"pull_request_review_id\":null,\"in_reply_to_id\":4049159495,"
        + "\"path\":\"frontend/src/components/workitem-v2/WorkItemModalV2.tsx\",\"line\":null,\"original_line\":92,"
        + "\"commit_id\":\"" + LaterCommit + "\",\"original_commit_id\":\"" + Head + "\","
        + "\"user\":{\"login\":\"tony\"},\"created_at\":\"2026-09-18T18:00:00Z\",\"body\":\"still broken\"}]";

    private static string IssueComments() =>
        "[{\"id\":3901234567,\"user\":{\"login\":\"tony\"},\"body\":\"ping\",\"created_at\":\"2026-09-18T19:00:00Z\"}]";

    // fullDatabaseId, not databaseId: a review comment id on this repository
    // (4049159495) does not fit in the 32-bit Int the deprecated field returns.
    private static string ReviewThreads() =>
        "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{"
        + "\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},"
        + "\"nodes\":[{\"id\":\"PRRT_kwDOS8ssMs5f1thread\",\"isResolved\":true,"
        + "\"comments\":{\"nodes\":[{\"fullDatabaseId\":\"4049159495\"},{\"fullDatabaseId\":\"4051372317\"}]}}]}}}}}";

    private static RoutingHandler GitHubPr() => new RoutingHandler()
        .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), Reviews)
        .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), ReviewComments)
        .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), IssueComments)
        .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), ReviewThreads)
        .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal),
            () => "{\"head\":{\"sha\":\"" + Head + "\"},\"state\":\"open\"}");

    [Fact]
    public async Task Every_inline_comment_comes_back_with_the_commit_it_was_written_against()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var ledger = await CreateService(db, GitHubPr()).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var comment = ledger.Items.Single(i => i.CommentId == "4049159495");
        Assert.Equal("frontend/src/components/workitem-v2/WorkItemModalV2.tsx", comment.Path);
        Assert.Equal(92, comment.Line);
        Assert.Equal("Copilot", comment.Author);
        Assert.Equal("5250768235", comment.ReviewId);
        // The commit the comment was made on, not the head it drifted onto.
        Assert.Equal(Head, comment.Commit);
        Assert.Contains("requestClose", comment.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_whose_line_has_moved_keeps_the_line_it_was_written_on()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var ledger = await CreateService(db, GitHubPr()).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.Equal(92, ledger.Items.Single(i => i.CommentId == "4051372317").Line);
    }

    [Fact]
    public async Task Comments_in_one_thread_share_its_id_and_its_resolved_state()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var ledger = await CreateService(db, GitHubPr()).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var root = ledger.Items.Single(i => i.CommentId == "4049159495");
        var reply = ledger.Items.Single(i => i.CommentId == "4051372317");
        Assert.Equal("PRRT_kwDOS8ssMs5f1thread", root.ThreadId);
        Assert.Equal(root.ThreadId, reply.ThreadId);
        Assert.True(root.Resolved);
        Assert.True(reply.Resolved);
    }

    [Fact]
    public async Task A_pull_request_level_comment_comes_back_with_no_file_and_its_own_id()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var ledger = await CreateService(db, GitHubPr()).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var issue = ledger.Items.Single(i => i.CommentId == "3901234567");
        Assert.Null(issue.Path);
        Assert.Equal("tony", issue.Author);
        Assert.Contains("ping", issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_review_body_hides_comes_back_as_items_too()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var ledger = await CreateService(db, GitHubPr()).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var suppressed = ledger.Items.Single(i => i.Path == "frontend/src/utils/attachments.ts");
        Assert.Equal(30, suppressed.Line);
        Assert.Equal("5250768235", suppressed.ReviewId);
        Assert.Equal(Head, suppressed.Commit);
        Assert.Contains("preserve it", suppressed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_review_comes_back_with_its_verdict_head_commit_and_whether_it_finished()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var ledger = await CreateService(db, GitHubPr()).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        var review = Assert.Single(ledger.Reviews);
        Assert.Equal("5250768235", review.Id);
        Assert.Equal("COMMENTED", review.State);
        Assert.Equal(Head, review.HeadSha);
        Assert.False(review.Incomplete);
        Assert.Equal(new DateTime(2026, 9, 18, 17, 34, 45, DateTimeKind.Utc), review.SubmittedAt.ToUniversalTime());
        // Without a head the per-head repeat suppression has nothing to key on.
        Assert.False(string.IsNullOrEmpty(ledger.HeadSha));
    }

    [Fact]
    public async Task A_review_the_reviewer_could_not_finish_is_flagged_on_the_ledger()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");
        var truncated = new RoutingHandler()
            .Map(u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), () =>
                "[{\"id\":5222030374,\"user\":{\"login\":\"copilot-pull-request-reviewer[bot]\"},\"state\":\"COMMENTED\","
                + "\"submitted_at\":\"2026-09-18T17:34:45Z\",\"commit_id\":\"" + Head + "\","
                + "\"body\":\"> [!NOTE]\\n> Copilot was unable to run its full agentic suite in this review.\\n\\n## Pull request overview\"}]")
            .Map(u => u.Contains("/pulls/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.Contains("/issues/7/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal), ReviewThreads)
            .Map(u => u.EndsWith("/pulls/7", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, truncated).GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");

        Assert.True(Assert.Single(ledger.Reviews).Incomplete);
    }

    [Fact]
    public async Task A_reply_is_posted_into_the_thread_of_the_comment_it_answers()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");
        var handler = new RoutingHandler()
            .Map(u => u.EndsWith("/replies", StringComparison.Ordinal), () => "{\"id\":4053396920}");

        var result = await CreateService(db, handler).ReplyToReviewThreadAsync(
            "https://github.com/team/repo", "7", "4049159495", "That compiles.");

        Assert.True(result.Ok);
        Assert.Equal("4053396920", result.Id);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.github.com/repos/team/repo/pulls/7/comments/4049159495/replies", call.Url);
        Assert.Contains("That compiles.", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_the_forge_accepted_without_naming_it_is_still_a_success()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");
        var handler = new RoutingHandler().Map(u => u.EndsWith("/replies", StringComparison.Ordinal), () => "{}");

        var result = await CreateService(db, handler).ReplyToReviewThreadAsync(
            "https://github.com/team/repo", "7", "4049159495", "That compiles.");

        Assert.True(result.Ok);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task Resolving_a_thread_goes_through_the_graphql_mutation()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");
        var handler = new RoutingHandler()
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal),
                () => "{\"data\":{\"resolveReviewThread\":{\"thread\":{\"isResolved\":true}}}}");

        var result = await CreateService(db, handler).ResolveReviewThreadAsync(
            "https://github.com/team/repo", "7", "PRRT_kwDOS8ssMs5f1thread");

        Assert.True(result.Ok);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://api.github.com/graphql", call.Url);
        Assert.Contains("resolveReviewThread", call.Body, StringComparison.Ordinal);
        Assert.Contains("PRRT_kwDOS8ssMs5f1thread", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mutation_the_forge_refuses_is_a_failure_even_though_it_answered_200()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");
        var handler = new RoutingHandler()
            .Map(u => u.EndsWith("/graphql", StringComparison.Ordinal),
                () => "{\"errors\":[{\"message\":\"Resource not accessible by integration\"}]}");

        var result = await CreateService(db, handler).ResolveReviewThreadAsync(
            "https://github.com/team/repo", "7", "PRRT_kwDOS8ssMs5f1thread");

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task A_provider_whose_api_cannot_resolve_threads_says_so_instead_of_claiming_success()
    {
        using var db = new TestDb();
        AddProvider(db, "Forgejo", "https://gitea.example");
        var handler = new RoutingHandler();

        var result = await CreateService(db, handler).ResolveReviewThreadAsync(
            "https://gitea.example/team/repo.git", "5", "thread-1");

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_repository_no_provider_matches_is_answered_not_thrown()
    {
        using var db = new TestDb();

        var service = CreateService(db, new RoutingHandler());
        var ledger = await service.GetPullRequestReviewLedgerAsync("https://nowhere.example/team/repo", "7");

        Assert.Empty(ledger.Items);
        Assert.False(string.IsNullOrWhiteSpace(ledger.Message));
        Assert.False((await service.ReplyToReviewThreadAsync("https://nowhere.example/team/repo", "7", "1", "hi")).Ok);
        Assert.False((await service.ResolveReviewThreadAsync("https://nowhere.example/team/repo", "7", "t1")).Ok);
    }

    [Fact]
    public async Task A_forge_that_will_not_answer_degrades_to_a_message()
    {
        using var db = new TestDb();
        AddProvider(db, "GitHub", "https://github.com");

        var service = CreateService(db, new ThrowingHandler());

        var ledger = await service.GetPullRequestReviewLedgerAsync("https://github.com/team/repo", "7");
        Assert.Empty(ledger.Items);
        Assert.False(string.IsNullOrWhiteSpace(ledger.Message));
        Assert.False((await service.ReplyToReviewThreadAsync("https://github.com/team/repo", "7", "1", "hi")).Ok);
        Assert.False((await service.ResolveReviewThreadAsync("https://github.com/team/repo", "7", "t1")).Ok);
    }
}
