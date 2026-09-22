using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Forgejo says "I do not have this" with a zero or an empty string rather than
/// by leaving the field out, so <c>original_position ?? position</c> and
/// <c>original_commit_id ?? commit_id</c> never fell through. Every inline
/// comment was recorded at line 0 on commit "", the reply went out with
/// <c>new_position: 0</c> — which Forgejo cannot place, so it rendered the
/// answer once per hunk line — and the empty commit broke both fresh-vs-
/// re-delivered detection and the one-round-per-head throttle.
///
/// The payload below is a real one, from Forgejo 9.0.3.
/// </summary>
public class ForgejoAbsentAsZeroTests
{
    private const string Repo = "https://forgejo.example/team/repo.git";
    private const string Commit = "9b531f43a5e7ebd52ea18f95784728765c9c9ad3";

    private const string RealComment = """
        {"id":125,"body":"I dont like this, remove it","path":"README.md",
         "commit_id":"9b531f43a5e7ebd52ea18f95784728765c9c9ad3","original_commit_id":"",
         "diff_hunk":"@@ -1,1 +0,4 @@\n\\ No newline at end of file\n+Hello world\n+\n+Test comment: this line was added for testing purposes.",
         "position":3,"original_position":0,"resolver":null,"pull_request_review_id":1,
         "created_at":"2026-09-22T13:45:24Z"}
        """;

    private sealed class Handler : HttpMessageHandler
    {
        private readonly List<(HttpMethod? Method, Func<string, bool> Match, Func<string> Body)> _rules = new();
        public List<string> Posted { get; } = new();

        public Handler Map(Func<string, bool> match, Func<string> body)
        {
            _rules.Add((null, match, body));
            return this;
        }

        public Handler MapMethod(HttpMethod method, Func<string, bool> match, Func<string> body)
        {
            _rules.Insert(0, (method, match, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (request.Content is not null && request.Method == HttpMethod.Post)
                Posted.Add(await request.Content.ReadAsStringAsync(ct));
            foreach (var (method, match, body) in _rules)
                if ((method is null || method == request.Method) && match(url))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body(), Encoding.UTF8, "application/json"),
                    };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static RemoteProviderService ServiceOn(TestDb db, Handler handler)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = "Forgejo", Type = "Forgejo", Url = "https://forgejo.example", ApiKey = "k",
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

    private static Handler Forge() => new Handler()
        .Map(u => u.Contains("/pulls/5/reviews/1/comments", StringComparison.Ordinal), () => "[" + RealComment + "]")
        .MapMethod(HttpMethod.Get, u => u.EndsWith("/pulls/5/reviews", StringComparison.Ordinal)
                || u.Contains("/pulls/5/reviews?", StringComparison.Ordinal),
            () => """[{"id":1,"state":"COMMENT","body":"","commit_id":"9b531f43a5e7ebd52ea18f95784728765c9c9ad3","submitted_at":"2026-09-22T13:45:00Z","user":{"login":"tony"}}]""")
        .Map(u => u.Contains("/issues/5/comments", StringComparison.Ordinal), () => "[]")
        .Map(u => u.Contains("/pulls/5", StringComparison.Ordinal),
            () => """{"head":{"sha":"9b531f43a5e7ebd52ea18f95784728765c9c9ad3"}}""");

    [Fact]
    public async Task A_zero_position_and_an_empty_commit_mean_absent_not_line_zero()
    {
        using var db = new TestDb();
        var ledger = await ServiceOn(db, Forge()).GetPullRequestReviewLedgerAsync(Repo, "5");

        var item = Assert.Single(ledger.Items, i => i.Kind == "review");
        Assert.Equal(3, item.Line);
        Assert.Equal(Commit, item.Commit);
        Assert.Equal("README.md", item.Path);
    }

    [Fact]
    public async Task A_reply_to_it_goes_out_on_the_line_the_comment_is_really_on()
    {
        // new_position: 0 is what Forgejo cannot place; it rendered the answer
        // once per line of the hunk on the files page.
        using var db = new TestDb();
        var handler = Forge()
            .MapMethod(HttpMethod.Post, u => u.EndsWith("/pulls/5/reviews", StringComparison.Ordinal),
                () => """{"id":91}""")
            .Map(u => u.Contains("/pulls/5/reviews/91/comments", StringComparison.Ordinal),
                () => """[{"id":5001,"path":"README.md","position":3,"body":"Removed."}]""");

        var posted = await ServiceOn(db, handler).ReplyToReviewThreadAsync(Repo, "5", "125", "Removed.");

        Assert.True(posted.Ok);
        var request = Assert.Single(handler.Posted, p => p.Contains("new_position", StringComparison.Ordinal));
        Assert.Contains("\"new_position\":3", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"new_position\":0", request, StringComparison.Ordinal);
    }
}
