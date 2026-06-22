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
