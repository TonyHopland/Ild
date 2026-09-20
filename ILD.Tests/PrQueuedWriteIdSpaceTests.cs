using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The id the PR node records for a reply is one half of the guard that stops
/// the loop answering itself, and it is only any use if it is an id from the
/// space that provider's own ledger keys on. Recording the wrong space is worse
/// than recording nothing: an id from another sequence can equal a real human
/// comment's id, and the ledger would then suppress that comment for ever and
/// report it as something ILD wrote.
///
/// Each case here answers the create/reply call with the shape the provider
/// really returns, and asserts the recorded key is the one the ledger produces
/// for that same comment.
/// </summary>
public class PrQueuedWriteIdSpaceTests
{
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod? Method, Func<string, bool> Match, Func<string> Body)> _rules = new();
        public List<string> Urls { get; } = new();

        public RoutingHandler Map(Func<string, bool> match, Func<string> body)
        {
            _rules.Add((null, match, body));
            return this;
        }

        /// <summary>A list GET and a create POST share a URL on both forges, so the method has to be part of the match.</summary>
        public RoutingHandler MapMethod(HttpMethod method, Func<string, bool> match, Func<string> body)
        {
            _rules.Insert(0, (method, match, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            foreach (var (method, match, body) in _rules)
                if ((method is null || method == request.Method) && match(url))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body(), Encoding.UTF8, "application/json"),
                    });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

    /// <summary>The key the PR node records for a reply, as it builds it.</summary>
    private static string RecordedKey(string id) => PrCommentLedger.KeyFor("review", id);

    /// <summary>The key the ledger produces for an item, as the throttle builds it.</summary>
    private static string LedgerKey(RemotePrReviewItem item)
        => PrCommentLedger.KeyFor(item.Kind, item.CommentId!);

    [Fact]
    public async Task A_forgejo_reply_records_the_comments_id_not_the_reviews()
    {
        // Gitea answers POST pulls/{n}/reviews with the REVIEW it created. Its
        // id is a different sequence from the comment ids the ledger keys on,
        // and 91 here is deliberately also a real review comment's id.
        const string repo = "https://gitea.example/team/repo.git";
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/5/reviews/91/comments", StringComparison.Ordinal), () =>
                "[{\"id\":5001,\"path\":\"src/A.cs\",\"position\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-20T12:00:00Z\",\"body\":\"That compiles.\"}]")
            .Map(u => u.Contains("/pulls/5/reviews/40/comments", StringComparison.Ordinal), () =>
                "[{\"id\":91,\"path\":\"src/A.cs\",\"position\":10,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"a human finding\"}]")
            .Map(u => u.Contains("/pulls/5/reviews", StringComparison.Ordinal), () =>
                "[{\"id\":40,\"state\":\"COMMENT\",\"commit_id\":\"" + Head + "\","
                + "\"submitted_at\":\"2026-09-18T17:34:45Z\",\"user\":{\"login\":\"alice\"}}]")
            .Map(u => u.Contains("/issues/5/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/pulls/5", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var service = CreateService(db, handler, "Forgejo", "https://gitea.example");
        var ledger = await service.GetPullRequestReviewLedgerAsync(repo, "5");
        var humanComment = ledger.Items.Single(i => i.CommentId == "91");

        // The POST answers with the REVIEW it created; 91 is its id, and the
        // same number is already a real review comment on this pull request.
        handler.MapMethod(HttpMethod.Post, u => u.EndsWith("/pulls/5/reviews", StringComparison.Ordinal),
            () => "{\"id\":91,\"state\":\"COMMENT\",\"submitted_at\":\"2026-09-20T12:00:00Z\",\"user\":{\"login\":\"ild\"}}");
        var reply = await service.ReplyToReviewThreadAsync(repo, "5", "91", "That compiles.");

        Assert.True(reply.Ok);
        Assert.Equal("5001", reply.Id);
        // The decisive assertion: what the node would record must not be the key
        // the ledger gives a human's comment.
        Assert.NotEqual(LedgerKey(humanComment), RecordedKey(reply.Id!));
    }

    [Fact]
    public async Task A_forgejo_reply_whose_comment_cannot_be_read_records_nothing_rather_than_a_guess()
    {
        const string repo = "https://gitea.example/team/repo.git";
        using var db = new TestDb();
        var handler = new RoutingHandler()
            .Map(u => u.Contains("/pulls/5/reviews/40/comments", StringComparison.Ordinal), () =>
                "[{\"id\":91,\"path\":\"src/A.cs\",\"position\":10,\"user\":{\"login\":\"alice\"},"
                + "\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"a human finding\"}]")
            .Map(u => u.Contains("/pulls/5/reviews", StringComparison.Ordinal), () =>
                "[{\"id\":40,\"state\":\"COMMENT\",\"submitted_at\":\"2026-09-18T17:34:45Z\",\"user\":{\"login\":\"alice\"}}]")
            .Map(u => u.Contains("/issues/5/comments", StringComparison.Ordinal), () => "[]")
            .Map(u => u.EndsWith("/pulls/5", StringComparison.Ordinal), () => "{\"head\":{\"sha\":\"" + Head + "\"}}");

        var reply = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .ReplyToReviewThreadAsync(repo, "5", "91", "That compiles.");

        // The post was accepted; only its id could not be established.
        Assert.True(reply.Ok);
        Assert.Null(reply.Id);
    }

    [Fact]
    public async Task An_azure_pull_request_comment_records_the_id_the_ledger_keys_on()
    {
        const string repo = "https://dev.azure.com/org/project/_git/repo";
        using var db = new TestDb();
        var created = "{\"id\":77,\"status\":\"active\",\"comments\":[{\"id\":1,\"content\":\"Answered.\","
            + "\"commentType\":\"text\",\"author\":{\"displayName\":\"ILD\"},\"publishedDate\":\"2026-09-20T12:00:00Z\"}]}";
        var handler = new RoutingHandler()
            // Creating a thread answers with the thread itself; listing wraps them in "value".
            .MapMethod(HttpMethod.Post, u => u.Contains("/pullrequests/7/threads?", StringComparison.Ordinal), () => created)
            .Map(u => u.Contains("/pullrequests/7/threads?", StringComparison.Ordinal), () => "{\"value\":[" + created + "]}")
            .Map(u => u.Contains("/pullrequests/7?", StringComparison.Ordinal),
                () => "{\"lastMergeSourceCommit\":{\"commitId\":\"" + Head + "\"}}");

        var service = CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org");
        var posted = await service.CreatePullRequestCommentAsync(repo, "7", "Answered.");
        var ledger = await service.GetPullRequestReviewLedgerAsync(repo, "7");

        Assert.True(posted.Ok);
        var item = Assert.Single(ledger.Items);
        // The node records issue:<id>; the ledger keys this very comment the
        // same way, so the guard actually matches.
        Assert.Equal(PrCommentLedger.KeyFor(item.Kind, item.CommentId!), PrCommentLedger.KeyFor("issue", posted.Id!));
    }
}
