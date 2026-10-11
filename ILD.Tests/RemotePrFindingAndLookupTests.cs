using System.Net;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The provider side of line-anchored review findings and of finding the pull
/// request a branch already has. Each fake answers with the shape the forge
/// documents for that route — GitHub's REST reference, Forgejo's API source and
/// swagger, and Azure DevOps' 7.1 REST reference — and the list fakes filter
/// the way the real route does, so an adapter that leans on a filter the forge
/// does not apply picks the wrong pull request here too.
/// </summary>
public class RemotePrFindingAndLookupTests
{
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    private const string GitHubRepo = "https://github.com/team/repo";
    private const string GitHubApi = "https://api.github.com/repos/team/repo";
    private const string ForgejoRepo = "https://gitea.example/team/repo.git";
    private const string ForgejoApi = "https://gitea.example/api/v1/repos/team/repo";
    private const string AzureRepo = "https://dev.azure.com/contoso/widgets/_git/app";
    private const string AzureGitApi = "https://dev.azure.com/contoso/widgets/_apis/git/repositories/app";

    private sealed class ForgeHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, Func<string, bool> Match, Func<string, (HttpStatusCode Status, string Body)> Respond)> _rules = new();
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = new();

        public ForgeHandler On(HttpMethod method, Func<string, bool> match, string body, HttpStatusCode status = HttpStatusCode.OK)
            => On(method, match, _ => (status, body));

        public ForgeHandler On(HttpMethod method, Func<string, bool> match, Func<string, (HttpStatusCode, string)> respond)
        {
            _rules.Add((method, match, respond));
            return this;
        }

        public (HttpMethod Method, string Url, string Body) Single(HttpMethod method) => Assert.Single(Calls, c => c.Method == method);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, url, body));
            foreach (var (method, match, respond) in _rules)
            {
                if (method != request.Method || !match(url)) continue;
                var (status, responseBody) = respond(url);
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
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

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static string Query(string url, string name)
    {
        var at = url.IndexOf('?');
        if (at < 0) return string.Empty;
        foreach (var pair in url[(at + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name) return pair[(eq + 1)..];
        }
        return string.Empty;
    }

    // ---- Posting a finding as a line-anchored review comment ----

    [Fact]
    public async Task A_github_finding_is_a_review_comment_on_the_new_side_of_the_line_at_the_head_commit()
    {
        using var db = new TestDb();
        var handler = new ForgeHandler()
            .On(HttpMethod.Get, u => u.EndsWith("/pulls/7", StringComparison.Ordinal),
                "{\"number\":7,\"state\":\"open\",\"head\":{\"ref\":\"feature\",\"sha\":\"" + Head + "\"},\"base\":{\"ref\":\"main\"}}")
            .On(HttpMethod.Post, u => u.EndsWith("/pulls/7/comments", StringComparison.Ordinal),
                "{\"id\":4049159495,\"path\":\"src/A.cs\",\"line\":12,\"side\":\"RIGHT\",\"commit_id\":\"" + Head + "\","
                + "\"body\":\"Null is not handled.\",\"user\":{\"login\":\"ild\"}}",
                HttpStatusCode.Created);

        var result = await CreateService(db, handler, "GitHub", "https://github.com")
            .CreateReviewCommentAsync(GitHubRepo, "7", "src/A.cs", 12, "Null is not handled.");

        Assert.True(result.Ok);
        Assert.Equal("4049159495", result.Id);
        var post = handler.Single(HttpMethod.Post);
        Assert.Equal($"{GitHubApi}/pulls/7/comments", post.Url);
        var sent = Json(post.Body);
        Assert.Equal("src/A.cs", sent.GetProperty("path").GetString());
        Assert.Equal(12, sent.GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", sent.GetProperty("side").GetString());
        Assert.Equal(Head, sent.GetProperty("commit_id").GetString());
        Assert.Contains("Null is not handled.", sent.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forgejo_finding_is_a_one_comment_review_on_the_new_line_and_records_the_comments_id()
    {
        using var db = new TestDb();
        // The POST answers with the REVIEW; 91 is its id, a different sequence
        // from the comment ids the ledger keys on.
        var handler = new ForgeHandler()
            .On(HttpMethod.Post, u => u.EndsWith("/pulls/5/reviews", StringComparison.Ordinal),
                "{\"id\":91,\"state\":\"COMMENT\",\"commit_id\":\"" + Head + "\",\"submitted_at\":\"2026-09-20T12:00:00Z\",\"user\":{\"login\":\"ild\"}}")
            .On(HttpMethod.Get, u => u.Contains("/pulls/5/reviews/91/comments", StringComparison.Ordinal),
                "[{\"id\":5001,\"path\":\"src/A.cs\",\"position\":12,\"commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-20T12:00:00Z\",\"body\":\"Null is not handled.\"}]");

        var result = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .CreateReviewCommentAsync(ForgejoRepo, "5", "src/A.cs", 12, "Null is not handled.");

        Assert.True(result.Ok);
        Assert.Equal("5001", result.Id);
        var post = handler.Single(HttpMethod.Post);
        Assert.Equal($"{ForgejoApi}/pulls/5/reviews", post.Url);
        var sent = Json(post.Body);
        Assert.Equal("COMMENT", sent.GetProperty("event").GetString());
        var comment = Assert.Single(sent.GetProperty("comments").EnumerateArray());
        Assert.Equal("src/A.cs", comment.GetProperty("path").GetString());
        Assert.Equal(12, comment.GetProperty("new_position").GetInt32());
        Assert.Contains("Null is not handled.", comment.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_azure_finding_is_a_thread_anchored_on_the_right_file_and_records_the_ledgers_id()
    {
        using var db = new TestDb();
        var handler = new ForgeHandler()
            .On(HttpMethod.Post, u => u.StartsWith($"{AzureGitApi}/pullrequests/7/threads?", StringComparison.Ordinal),
                "{\"id\":77,\"status\":\"active\",\"threadContext\":{\"filePath\":\"/src/A.cs\","
                + "\"rightFileStart\":{\"line\":12,\"offset\":1},\"rightFileEnd\":{\"line\":12,\"offset\":1}},"
                + "\"comments\":[{\"id\":1,\"parentCommentId\":0,\"content\":\"Null is not handled.\",\"commentType\":\"text\","
                + "\"author\":{\"displayName\":\"ILD\"},\"publishedDate\":\"2026-09-20T12:00:00Z\"}]}");

        var result = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/contoso")
            .CreateReviewCommentAsync(AzureRepo, "7", "src/A.cs", 12, "Null is not handled.");

        Assert.True(result.Ok);
        // <thread>-<comment>, the shape the Azure ledger keys a comment on.
        Assert.Equal("77-1", result.Id);
        var sent = Json(handler.Single(HttpMethod.Post).Body);
        var context = sent.GetProperty("threadContext");
        Assert.Equal("/src/A.cs", context.GetProperty("filePath").GetString());
        Assert.Equal(12, context.GetProperty("rightFileStart").GetProperty("line").GetInt32());
        Assert.Equal(12, context.GetProperty("rightFileEnd").GetProperty("line").GetInt32());
        var comment = Assert.Single(sent.GetProperty("comments").EnumerateArray());
        Assert.Contains("Null is not handled.", comment.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string, string, string> RefusedFindings => new()
    {
        // GitHub refuses a line outside the diff with a 422.
        {
            "GitHub", "https://github.com", GitHubRepo, "/pulls/7/comments",
            "{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"PullRequestReviewComment\",\"code\":\"custom\","
            + "\"field\":\"pull_request_review_thread.line\",\"message\":\"could not be resolved\"}],"
            + "\"documentation_url\":\"https://docs.github.com/rest/pulls/comments#create-a-review-comment-for-a-pull-request\",\"status\":\"422\"}"
        },
        {
            "Forgejo", "https://gitea.example", ForgejoRepo, "/pulls/7/reviews",
            "{\"message\":\"position is out of range\",\"url\":\"https://gitea.example/api/swagger\"}"
        },
        {
            "AzureDevOps", "https://dev.azure.com/contoso", AzureRepo, "/pullrequests/7/threads?",
            "{\"$id\":\"1\",\"innerException\":null,\"message\":\"The thread context is invalid.\","
            + "\"typeName\":\"Microsoft.TeamFoundation.SourceControl.WebServer.InvalidArgumentValueException\","
            + "\"typeKey\":\"InvalidArgumentValueException\",\"errorCode\":0,\"eventId\":3000}"
        },
    };

    [Theory]
    [MemberData(nameof(RefusedFindings))]
    public async Task A_finding_the_forge_refuses_is_a_failed_write_not_a_throw(
        string type, string providerUrl, string repoUrl, string postRoute, string refusal)
    {
        using var db = new TestDb();
        var handler = new ForgeHandler()
            .On(HttpMethod.Get, u => u.Contains("/pulls/7", StringComparison.Ordinal) || u.Contains("/pullrequests/7", StringComparison.Ordinal),
                "{\"number\":7,\"pullRequestId\":7,\"state\":\"open\",\"head\":{\"ref\":\"feature\",\"sha\":\"" + Head + "\"},"
                + "\"lastMergeSourceCommit\":{\"commitId\":\"" + Head + "\"}}")
            .On(HttpMethod.Post, u => u.Contains(postRoute, StringComparison.Ordinal), refusal,
                type == "AzureDevOps" ? HttpStatusCode.BadRequest : HttpStatusCode.UnprocessableEntity);

        var result = await CreateService(db, handler, type, providerUrl)
            .CreateReviewCommentAsync(repoUrl, "7", "src/A.cs", 9999, "Null is not handled.");

        Assert.False(result.Ok);
        Assert.Null(result.Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    // ---- Finding the open pull request a branch already has ----

    private static string GitHubPull(int number, string head, string @base) =>
        "{\"number\":" + number + ",\"state\":\"open\",\"url\":\"" + GitHubApi + "/pulls/" + number + "\","
        + "\"html_url\":\"https://github.com/team/repo/pull/" + number + "\","
        + "\"head\":{\"label\":\"team:" + head + "\",\"ref\":\"" + head + "\",\"sha\":\"" + Head + "\"},"
        + "\"base\":{\"label\":\"team:" + @base + "\",\"ref\":\"" + @base + "\"}}";

    /// <summary>
    /// GitHub's <c>GET pulls</c>: <c>head=owner:branch</c> narrows the list to
    /// that head, and without it every open pull request comes back.
    /// </summary>
    private static ForgeHandler GitHubPulls(params (int Number, string Head, string Base)[] open)
        => new ForgeHandler().On(HttpMethod.Get, u => u.StartsWith($"{GitHubApi}/pulls?", StringComparison.Ordinal), url =>
        {
            var head = Query(url, "head");
            var page = Query(url, "page");
            var matching = page is "" or "1"
                ? open.Where(p => head.Length == 0 || $"team:{p.Head}" == head)
                : Enumerable.Empty<(int Number, string Head, string Base)>();
            return (HttpStatusCode.OK, "[" + string.Join(",", matching.Select(p => GitHubPull(p.Number, p.Head, p.Base))) + "]");
        });

    [Fact]
    public async Task A_github_branch_with_an_open_pull_request_is_found()
    {
        using var db = new TestDb();
        var handler = GitHubPulls((3, "other", "main"), (12, "feature", "main"));

        var found = await CreateService(db, handler, "GitHub", "https://github.com")
            .FindOpenPullRequestAsync(GitHubRepo, "feature", "main");

        Assert.Null(found.Error);
        Assert.Equal(12, found.Number);
        Assert.Equal("https://github.com/team/repo/pull/12", found.HtmlUrl);
    }

    [Fact]
    public async Task A_github_branch_with_no_open_pull_request_finds_none_without_an_error()
    {
        using var db = new TestDb();
        var handler = GitHubPulls((3, "other", "main"));

        var found = await CreateService(db, handler, "GitHub", "https://github.com")
            .FindOpenPullRequestAsync(GitHubRepo, "feature", "main");

        Assert.Null(found.Number);
        Assert.Null(found.Error);
    }

    [Fact]
    public async Task A_github_branch_open_against_several_bases_takes_the_one_into_the_target()
    {
        using var db = new TestDb();
        var handler = GitHubPulls((311, "feature", "release"), (312, "feature", "main"));

        var found = await CreateService(db, handler, "GitHub", "https://github.com")
            .FindOpenPullRequestAsync(GitHubRepo, "feature", "main");

        Assert.Null(found.Error);
        Assert.Equal(312, found.Number);
    }

    [Fact]
    public async Task A_github_branch_open_against_several_other_bases_is_an_error_naming_them()
    {
        using var db = new TestDb();
        var handler = GitHubPulls((311, "feature", "release"), (312, "feature", "hotfix"));

        var found = await CreateService(db, handler, "GitHub", "https://github.com")
            .FindOpenPullRequestAsync(GitHubRepo, "feature", "main");

        Assert.Null(found.Number);
        Assert.NotNull(found.Error);
        Assert.Contains("311", found.Error!, StringComparison.Ordinal);
        Assert.Contains("312", found.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Forgejo's <c>GET pulls</c> is paged, and a Forgejo of the vintage this
    /// adapter was verified against has no head filter, so the branch has to be
    /// picked out of the list by <c>head.ref</c>.
    /// </summary>
    private static ForgeHandler ForgejoPulls(params (int Number, string Head, string Base)[] open)
        => new ForgeHandler().On(HttpMethod.Get, u => u.StartsWith($"{ForgejoApi}/pulls?", StringComparison.Ordinal) || u == $"{ForgejoApi}/pulls", url =>
        {
            var page = Query(url, "page");
            var pulls = page is "" or "1" ? open : Array.Empty<(int Number, string Head, string Base)>();
            return (HttpStatusCode.OK, "[" + string.Join(",", pulls.Select(p =>
                "{\"number\":" + p.Number + ",\"state\":\"open\",\"url\":\"https://gitea.example/team/repo/pulls/" + p.Number + "\","
                + "\"html_url\":\"https://gitea.example/team/repo/pulls/" + p.Number + "\","
                + "\"head\":{\"label\":\"" + p.Head + "\",\"ref\":\"" + p.Head + "\",\"sha\":\"" + Head + "\"},"
                + "\"base\":{\"label\":\"" + p.Base + "\",\"ref\":\"" + p.Base + "\"}}")) + "]");
        });

    [Fact]
    public async Task A_forgejo_branch_with_an_open_pull_request_is_picked_out_of_the_list()
    {
        using var db = new TestDb();
        var handler = ForgejoPulls((3, "other", "main"), (5, "feature", "main"));

        var found = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .FindOpenPullRequestAsync(ForgejoRepo, "feature", "main");

        Assert.Null(found.Error);
        Assert.Equal(5, found.Number);
        Assert.Equal("https://gitea.example/team/repo/pulls/5", found.HtmlUrl);
    }

    [Fact]
    public async Task A_forgejo_branch_with_no_open_pull_request_finds_none_without_an_error()
    {
        using var db = new TestDb();
        var handler = ForgejoPulls((3, "other", "main"));

        var found = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .FindOpenPullRequestAsync(ForgejoRepo, "feature", "main");

        Assert.Null(found.Number);
        Assert.Null(found.Error);
    }

    /// <summary>
    /// Azure DevOps' <c>GET pullrequests</c>: <c>searchCriteria.sourceRefName</c>
    /// narrows by source branch, and status defaults to active.
    /// </summary>
    private static ForgeHandler AzurePulls(params (int Number, string Head, string Base)[] active)
        => new ForgeHandler().On(HttpMethod.Get, u => u.StartsWith($"{AzureGitApi}/pullrequests?", StringComparison.Ordinal), url =>
        {
            var source = Query(url, "searchCriteria.sourceRefName");
            var matching = active.Where(p => source.Length == 0 || $"refs/heads/{p.Head}" == source).ToList();
            return (HttpStatusCode.OK, "{\"value\":[" + string.Join(",", matching.Select(p =>
                "{\"pullRequestId\":" + p.Number + ",\"codeReviewId\":" + p.Number + ",\"status\":\"active\","
                + "\"sourceRefName\":\"refs/heads/" + p.Head + "\",\"targetRefName\":\"refs/heads/" + p.Base + "\","
                + "\"url\":\"" + AzureGitApi + "/pullRequests/" + p.Number + "\","
                + "\"lastMergeSourceCommit\":{\"commitId\":\"" + Head + "\"}}")) + "],\"count\":" + matching.Count + "}");
        });

    [Fact]
    public async Task An_azure_branch_with_an_active_pull_request_is_found()
    {
        using var db = new TestDb();
        var handler = AzurePulls((40, "other", "main"), (42, "feature", "main"));

        var found = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/contoso")
            .FindOpenPullRequestAsync(AzureRepo, "feature", "main");

        Assert.Null(found.Error);
        Assert.Equal(42, found.Number);
        Assert.Equal($"{AzureRepo}/pullrequest/42", found.HtmlUrl);
    }

    [Fact]
    public async Task An_azure_branch_with_no_active_pull_request_finds_none_without_an_error()
    {
        using var db = new TestDb();
        var handler = AzurePulls((40, "other", "main"));

        var found = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/contoso")
            .FindOpenPullRequestAsync(AzureRepo, "feature", "main");

        Assert.Null(found.Number);
        Assert.Null(found.Error);
    }

    [Theory]
    [InlineData("GitHub", "https://github.com", GitHubRepo)]
    [InlineData("Forgejo", "https://gitea.example", ForgejoRepo)]
    [InlineData("AzureDevOps", "https://dev.azure.com/contoso", AzureRepo)]
    public async Task A_forge_that_fails_the_lookup_is_an_error_never_no_pull_request(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();
        var handler = new ForgeHandler().On(HttpMethod.Get, _ => true, "{\"message\":\"Server Error\"}", HttpStatusCode.InternalServerError);

        var found = await CreateService(db, handler, type, providerUrl)
            .FindOpenPullRequestAsync(repoUrl, "feature", "main");

        Assert.Null(found.Number);
        Assert.NotNull(found.Error);
    }

    // ---- Reading a posted finding back off the forge ----

    private static readonly Guid RunId = Guid.Parse("2f0c3a0e-5d7b-4c55-9a51-0d6e1c1f7a10");
    private const string FindingId = "a3f1c09b2e7d4c68";

    // Longer than the ledger's per-item cap, so the stamp at its end is only
    // readable from the full body the forge returned.
    private static readonly string FindingBody = PrCommentMarker.Stamp(
        "Null is not handled. " + new string('x', 2000), RunId, FindingId, "important");

    private static readonly string ReplyBody = PrCommentMarker.Stamp("Fixed in the next push.", RunId);

    private static string J(string value) => JsonSerializer.Serialize(value);

    private static void AssertFindingReadBack(RemotePrReviewLedger ledger, string findingCommentId, params string[] otherCommentIds)
    {
        Assert.Null(ledger.Message);
        var finding = ledger.Items.Single(i => i.CommentId == findingCommentId);
        Assert.Equal(FindingId, finding.FindingId);
        Assert.Equal("important", finding.FindingSeverity);
        foreach (var id in otherCommentIds)
        {
            var other = ledger.Items.Single(i => i.CommentId == id);
            Assert.Null(other.FindingId);
            Assert.Null(other.FindingSeverity);
        }
    }

    [Fact]
    public async Task A_github_ledger_reads_the_finding_id_back_off_a_finding_ild_posted()
    {
        using var db = new TestDb();
        var handler = new ForgeHandler()
            .On(HttpMethod.Get, u => u.Contains("/pulls/7/reviews", StringComparison.Ordinal), "[]")
            .On(HttpMethod.Get, u => u.Contains("/pulls/7/comments", StringComparison.Ordinal),
                "[{\"id\":101,\"pull_request_review_id\":9001,\"path\":\"src/A.cs\",\"line\":12,\"original_line\":12,"
                + "\"commit_id\":\"" + Head + "\",\"original_commit_id\":\"" + Head + "\",\"user\":{\"login\":\"ild\"},"
                + "\"created_at\":\"2026-09-20T12:00:00Z\",\"body\":" + J(FindingBody) + "},"
                + "{\"id\":102,\"pull_request_review_id\":9002,\"path\":\"src/A.cs\",\"line\":20,\"original_line\":20,"
                + "\"commit_id\":\"" + Head + "\",\"original_commit_id\":\"" + Head + "\",\"user\":{\"login\":\"ild\"},"
                + "\"created_at\":\"2026-09-20T12:01:00Z\",\"body\":\"Rename this.\"}]")
            .On(HttpMethod.Get, u => u.Contains("/issues/7/comments", StringComparison.Ordinal),
                "[{\"id\":201,\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-20T12:02:00Z\",\"body\":" + J(ReplyBody) + "}]")
            .On(HttpMethod.Post, u => u.EndsWith("/graphql", StringComparison.Ordinal),
                "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},"
                + "\"nodes\":[{\"id\":\"PRRT_one\",\"isResolved\":false,\"comments\":{\"nodes\":[{\"fullDatabaseId\":\"101\"}]}},"
                + "{\"id\":\"PRRT_two\",\"isResolved\":false,\"comments\":{\"nodes\":[{\"fullDatabaseId\":\"102\"}]}}]}}}}}")
            .On(HttpMethod.Get, u => u.EndsWith("/pulls/7", StringComparison.Ordinal),
                "{\"number\":7,\"state\":\"open\",\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "GitHub", "https://github.com")
            .GetPullRequestReviewLedgerAsync(GitHubRepo, "7");

        AssertFindingReadBack(ledger, "101", "102", "201");
    }

    [Fact]
    public async Task A_forgejo_ledger_reads_the_finding_id_back_off_a_finding_ild_posted()
    {
        using var db = new TestDb();
        var handler = new ForgeHandler()
            .On(HttpMethod.Get, u => u.Contains("/pulls/5/reviews/91/comments", StringComparison.Ordinal),
                "[{\"id\":5001,\"path\":\"src/A.cs\",\"position\":12,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-20T12:00:00Z\",\"body\":" + J(FindingBody) + "}]")
            .On(HttpMethod.Get, u => u.Contains("/pulls/5/reviews/40/comments", StringComparison.Ordinal),
                "[{\"id\":5002,\"path\":\"src/A.cs\",\"position\":20,\"original_commit_id\":\"" + Head + "\","
                + "\"user\":{\"login\":\"alice\"},\"created_at\":\"2026-09-18T17:34:44Z\",\"body\":\"Rename this.\"}]")
            .On(HttpMethod.Get, u => u.Contains("/pulls/5/reviews", StringComparison.Ordinal),
                "[{\"id\":40,\"state\":\"COMMENT\",\"commit_id\":\"" + Head + "\",\"submitted_at\":\"2026-09-18T17:34:45Z\",\"user\":{\"login\":\"alice\"}},"
                + "{\"id\":91,\"state\":\"COMMENT\",\"commit_id\":\"" + Head + "\",\"submitted_at\":\"2026-09-20T12:00:00Z\",\"user\":{\"login\":\"ild\"}}]")
            .On(HttpMethod.Get, u => u.Contains("/issues/5/comments", StringComparison.Ordinal),
                "[{\"id\":6001,\"user\":{\"login\":\"ild\"},\"created_at\":\"2026-09-20T12:02:00Z\",\"body\":" + J(ReplyBody) + "}]")
            .On(HttpMethod.Get, u => u.EndsWith("/pulls/5", StringComparison.Ordinal),
                "{\"number\":5,\"state\":\"open\",\"head\":{\"sha\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "Forgejo", "https://gitea.example")
            .GetPullRequestReviewLedgerAsync(ForgejoRepo, "5");

        AssertFindingReadBack(ledger, "5001", "5002", "6001");
    }

    [Fact]
    public async Task An_azure_ledger_reads_the_finding_id_back_off_a_finding_ild_posted()
    {
        using var db = new TestDb();
        string AzureThread(int id, string? path, string content) =>
            "{\"id\":" + id + ",\"status\":\"active\","
            + (path is null ? "" : "\"threadContext\":{\"filePath\":\"" + path + "\",\"rightFileStart\":{\"line\":12,\"offset\":1},\"rightFileEnd\":{\"line\":12,\"offset\":1}},")
            + "\"comments\":[{\"id\":1,\"parentCommentId\":0,\"content\":" + J(content) + ",\"commentType\":\"text\","
            + "\"author\":{\"displayName\":\"ILD\"},\"publishedDate\":\"2026-09-20T12:00:00Z\"}]}";
        var handler = new ForgeHandler()
            .On(HttpMethod.Get, u => u.StartsWith($"{AzureGitApi}/pullrequests/7/threads?", StringComparison.Ordinal),
                "{\"value\":[" + AzureThread(77, "/src/A.cs", FindingBody) + "," + AzureThread(78, "/src/A.cs", "Rename this.") + ","
                + AzureThread(79, null, ReplyBody) + "],\"count\":3}")
            .On(HttpMethod.Get, u => u.StartsWith($"{AzureGitApi}/pullrequests/7?", StringComparison.Ordinal),
                "{\"pullRequestId\":7,\"status\":\"active\",\"lastMergeSourceCommit\":{\"commitId\":\"" + Head + "\"}}");

        var ledger = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/contoso")
            .GetPullRequestReviewLedgerAsync(AzureRepo, "7");

        AssertFindingReadBack(ledger, "77-1", "78-1", "79-1");
    }
}
