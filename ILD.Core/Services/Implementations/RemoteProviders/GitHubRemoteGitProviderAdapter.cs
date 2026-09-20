using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

public sealed class GitHubRemoteGitProviderAdapter : RemoteGitProviderAdapterBase
{
    private const string GitHubApiAcceptHeader = "application/vnd.github+json";

    public override string ProviderType => "GitHub";
    public override string WebhookRouteSegment => "github";
    protected override string SignatureHeaderName => "X-Hub-Signature-256";

    protected override bool HostMatches(Uri providerUri, Uri repoUri)
        => NormalizeGitHubHost(providerUri.Host).Equals(NormalizeGitHubHost(repoUri.Host), StringComparison.OrdinalIgnoreCase);

    public override async Task<bool> MergePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.PutAsJsonAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/merge", new { });
        return resp.IsSuccessStatusCode;
    }

    public override async Task<bool> EnablePullRequestAutoMergeAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);

        // Auto-merge is a GraphQL-only mutation keyed by the PR's global node id,
        // which the REST PR resource carries as "node_id".
        using var prResp = await http.GetAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}");
        if (!prResp.IsSuccessStatusCode)
            return false;
        using var prDoc = JsonDocument.Parse(await prResp.Content.ReadAsStringAsync());
        if (!prDoc.RootElement.TryGetProperty("node_id", out var nodeId) || nodeId.ValueKind != JsonValueKind.String)
            return false;

        const string mutation = "mutation($pr: ID!) { enablePullRequestAutoMerge(input: { pullRequestId: $pr }) { clientMutationId } }";
        using var resp = await http.PostAsJsonAsync(
            GraphQlEndpoint(repo.ApiBase),
            new { query = mutation, variables = new { pr = nodeId.GetString() } });
        if (!resp.IsSuccessStatusCode)
            return false;

        // GitHub returns HTTP 200 with an "errors" array when the repository has
        // auto-merge disabled; treat that as "not supported" rather than success.
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return !doc.RootElement.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0;
    }

    public override async Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
    {
        ApplyHeaders(http, repo.Provider);
        await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/hooks",
            new
            {
                name = "web",
                active = true,
                events = new[] { "pull_request", "pull_request_review", "issue_comment" },
                config = new
                {
                    url = callbackUrl,
                    content_type = "json",
                    secret = repo.Provider.WebhookSecret,
                },
            });
    }

    public override async Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName)
    {
        ApplyHeaders(http, repo.Provider);
        var branchRef = Uri.EscapeDataString(branchName);
        using var resp = await http.DeleteAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/git/refs/heads/{branchRef}");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// The plain-text log of one Actions job, fetched with the provider's token
    /// — the credential the agent asking for it does not hold. GitHub answers
    /// the logs endpoint with a redirect to a short-lived signed blob URL, which
    /// the handler follows; a job whose logs have expired or which never ran
    /// under Actions (a third-party check run) is reported as unavailable rather
    /// than as a failure, since there is nothing the caller can do differently.
    /// </summary>
    public override async Task<RemoteCiLog> GetCheckLogAsync(
        HttpClient http, ResolvedRemoteRepository repo, string checkId, int tailLines, int offset)
    {
        ApplyHeaders(http, repo.Provider);

        var jobId = Uri.EscapeDataString(checkId);
        // Headers-first: the body is a job log, read as a stream and reduced to
        // the requested window line by line rather than buffered whole.
        using var resp = await http.GetAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/actions/jobs/{jobId}/logs",
            HttpCompletionOption.ResponseHeadersRead);

        if (!resp.IsSuccessStatusCode)
            return RemoteCiLog.Unavailable(resp.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "No log for this check — GitHub keeps job logs for a limited time, and checks published by apps other than Actions have none to fetch."
                : $"Could not read the log for this check (HTTP {(int)resp.StatusCode}).");

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var window = await WindowAsync(stream, tailLines, offset);
        return window.TotalLines == 0
            ? RemoteCiLog.Unavailable("The log for this check is empty.")
            : window;
    }

    public override bool SupportsThreadResolution => true;

    /// <summary>The review ledger is GitHub's REST shape, so this is where it is opted into.</summary>
    public override Task<RemotePrReviewLedger> GetPullRequestReviewLedgerAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
        => ReadRestReviewLedgerAsync(http, repo, prNumber);

    public override Task<RemotePrWriteResult> ReplyToReviewThreadAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber, string commentId, string body)
        => PostRestThreadReplyAsync(http, repo, prNumber, commentId, body);

    private const string ReviewThreadsQuery =
        "query($owner: String!, $repo: String!, $number: Int!, $cursor: String) { "
        + "repository(owner: $owner, name: $repo) { pullRequest(number: $number) { "
        + "reviewThreads(first: 100, after: $cursor) { pageInfo { hasNextPage endCursor } "
        + "nodes { id isResolved comments(first: 100) { nodes { fullDatabaseId } } } } } } }";

    /// <summary>
    /// Page ceiling, so a pathological pull request cannot hold the heartbeat
    /// open: 100 threads a page covers far more than any PR a loop opens carries.
    /// </summary>
    private const int MaxReviewThreadPages = 20;

    /// <summary>
    /// Review threads and their resolved state, which REST does not expose at
    /// all — a comment resource says nothing about the thread it sits in. Comment
    /// ids come back as <c>fullDatabaseId</c>, not the deprecated
    /// <c>databaseId</c>: this repository's own review comment ids overflow the
    /// 32-bit Int that field returns.
    ///
    /// Threads are walked to the end; the comments within one thread are not,
    /// and stop at GraphQL's own maximum of 100. Past that the tail of a single
    /// conversation falls back to the root of its reply chain for a thread id
    /// and reports unresolved — those comments are still read and still
    /// delivered, so it costs the reply route on one very long thread, not the
    /// item.
    /// </summary>
    protected override async Task<IReadOnlyList<RemotePrReviewThread>> GetReviewThreadsAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        if (!int.TryParse(prNumber, out var number))
            return Array.Empty<RemotePrReviewThread>();

        ApplyHeaders(http, repo.Provider);
        var threads = new List<RemotePrReviewThread>();
        string? cursor = null;

        for (var page = 0; page < MaxReviewThreadPages; page++)
        {
            using var resp = await http.PostAsJsonAsync(
                GraphQlEndpoint(repo.ApiBase),
                new
                {
                    query = ReviewThreadsQuery,
                    variables = new { owner = repo.Owner, repo = repo.Repo, number, cursor },
                });
            if (!resp.IsSuccessStatusCode)
                break;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!TryGetPath(doc.RootElement, out var reviewThreads,
                    "data", "repository", "pullRequest", "reviewThreads"))
                break;

            if (reviewThreads.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    var threadId = node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;
                    if (threadId is null) continue;
                    threads.Add(new RemotePrReviewThread(
                        threadId,
                        ReadCommentIds(node),
                        node.TryGetProperty("isResolved", out var resolved) && resolved.ValueKind == JsonValueKind.True));
                }
            }

            if (!TryGetPath(reviewThreads, out var pageInfo, "pageInfo")
                || !pageInfo.TryGetProperty("hasNextPage", out var hasNext)
                || hasNext.ValueKind != JsonValueKind.True)
                break;
            cursor = pageInfo.TryGetProperty("endCursor", out var end) && end.ValueKind == JsonValueKind.String
                ? end.GetString()
                : null;
            if (cursor is null)
                break;
        }

        return threads;
    }

    /// <summary>
    /// Resolving a thread is a GraphQL-only mutation keyed by the thread's global
    /// node id, which is the id <see cref="GetReviewThreadsAsync"/> reports.
    /// </summary>
    public override async Task<RemotePrWriteResult> ResolveReviewThreadAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber, string threadId)
    {
        ApplyHeaders(http, repo.Provider);

        const string mutation = "mutation($thread: ID!) { resolveReviewThread(input: { threadId: $thread }) { thread { isResolved } } }";
        using var resp = await http.PostAsJsonAsync(
            GraphQlEndpoint(repo.ApiBase),
            new { query = mutation, variables = new { thread = threadId } });
        if (!resp.IsSuccessStatusCode)
            return new RemotePrWriteResult(false, null, $"GitHub refused to resolve the thread (HTTP {(int)resp.StatusCode}).");

        // GitHub answers 200 with an "errors" array when the credentials may not
        // resolve threads; that is a refusal, not a success.
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var error = FirstGraphQlError(doc.RootElement);
        return error is null
            ? new RemotePrWriteResult(true, threadId, null)
            : new RemotePrWriteResult(false, null, $"GitHub refused to resolve the thread: {error}");
    }

    private static IReadOnlyList<string> ReadCommentIds(JsonElement thread)
    {
        var ids = new List<string>();
        if (!TryGetPath(thread, out var nodes, "comments", "nodes") || nodes.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var comment in nodes.EnumerateArray())
        {
            if (!comment.TryGetProperty("fullDatabaseId", out var id)) continue;
            var value = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            };
            if (value is not null) ids.Add(value);
        }

        return ids;
    }

    private static string? FirstGraphQlError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
            return null;

        var first = errors[0];
        return first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : "the API reported an error";
    }

    private static bool TryGetPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    public override WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var repoId = GetRepositoryId(root);
        var eventName = GetHeader(headers, "X-GitHub-Event");

        return eventName?.ToLowerInvariant() switch
        {
            "pull_request" => ParsePullRequestPayload(root, repoId),
            "issue_comment" => ParseIssueCommentPayload(root, repoId),
            "pull_request_review" => ParsePullRequestReviewPayload(root, repoId),
            _ => null,
        };
    }

    protected override void ApplyHeaders(HttpClient http, RemoteProvider provider)
    {
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.UserAgent.Clear();

        if (!string.IsNullOrEmpty(provider.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubApiAcceptHeader));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ILD", "1.0"));
    }

    protected override string BuildApiBase(Uri providerUri)
    {
        if (providerUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || providerUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return "https://api.github.com";

        var baseUrl = providerUri.ToString().TrimEnd('/');
        if (providerUri.AbsolutePath.TrimEnd('/').EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        return baseUrl + "/api/v3";
    }

    // The GraphQL endpoint sits beside the REST base: github.com →
    // https://api.github.com/graphql; GitHub Enterprise → .../api/graphql.
    private static string GraphQlEndpoint(string apiBase)
        => apiBase.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase)
            ? apiBase[..^"/api/v3".Length].TrimEnd('/') + "/api/graphql"
            : apiBase.TrimEnd('/') + "/graphql";

    private static string NormalizeGitHubHost(string host)
        => host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : host;

    private static WebhookPayload? ParsePullRequestPayload(JsonElement root, string repoId)
    {
        var pr = root.GetProperty("pull_request");
        var action = root.GetProperty("action").GetString();
        if (!string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase))
            return null;

        var merged = pr.TryGetProperty("merged", out var mergedValue) && mergedValue.GetBoolean();
        return new WebhookPayload(
            merged ? "pull_request.merged" : "pull_request.rejected",
            repoId,
            ReadString(pr, "number"),
            ReadString(pr, "html_url") ?? ReadString(pr, "url"),
            null,
            merged ? "merged" : "closed");
    }

    private static WebhookPayload? ParseIssueCommentPayload(JsonElement root, string repoId)
    {
        if (!root.TryGetProperty("issue", out var issue)
            || !issue.TryGetProperty("pull_request", out _)
            || !root.TryGetProperty("comment", out var comment))
            return null;

        return new WebhookPayload(
            "pull_request.comment",
            repoId,
            ReadString(issue, "number"),
            ReadString(issue, "html_url"),
            ReadString(comment, "body"),
            null);
    }

    private static WebhookPayload? ParsePullRequestReviewPayload(JsonElement root, string repoId)
    {
        if (!root.TryGetProperty("pull_request", out var pr)
            || !root.TryGetProperty("review", out var review))
            return null;

        var state = ReadString(review, "state");
        if (string.Equals(state, "changes_requested", StringComparison.OrdinalIgnoreCase))
        {
            return new WebhookPayload(
                "pull_request.rejected",
                repoId,
                ReadString(pr, "number"),
                ReadString(pr, "html_url") ?? ReadString(pr, "url"),
                ReadString(review, "body"),
                "changes_requested");
        }

        var comment = ReadString(review, "body");
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        return new WebhookPayload(
            "pull_request.review",
            repoId,
            ReadString(pr, "number"),
            ReadString(pr, "html_url") ?? ReadString(pr, "url"),
            comment,
            null);
    }

    private static string GetRepositoryId(JsonElement root)
        => root.TryGetProperty("repository", out var repository)
            ? ReadString(repository, "id") ?? ReadString(repository, "full_name") ?? ReadString(repository, "name") ?? "unknown"
            : "unknown";

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }
}
