using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

public sealed class GitHubRemoteGitProviderAdapter : IRemoteGitProviderAdapter
{
    private const string GitHubApiAcceptHeader = "application/vnd.github+json";

    public string ProviderType => "GitHub";
    public string WebhookRouteSegment => "github";

    public ResolvedRemoteRepository? TryResolve(RemoteProvider provider, Uri repoUri)
    {
        if (!string.Equals(provider.Type, ProviderType, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(provider.Url, UriKind.Absolute, out var providerUri))
            return null;

        if (!NormalizeGitHubHost(providerUri.Host).Equals(NormalizeGitHubHost(repoUri.Host), StringComparison.OrdinalIgnoreCase))
            return null;

        var path = repoUri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', 2);
        if (parts.Length < 2)
            return null;

        return new ResolvedRemoteRepository(
            provider,
            ProviderType,
            BuildApiBase(providerUri),
            parts[0],
            parts[1],
            this);
    }

    public async Task<RemotePrResult> CreatePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string sourceBranch, string targetBranch, string title, string body)
    {
        ApplyHeaders(http, repo.Provider);

        using var resp = await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls",
            new { title, body, head = sourceBranch, @base = targetBranch });

        if (!resp.IsSuccessStatusCode)
            return new RemotePrResult(null, null, RemotePrStatus.Open, $"HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new RemotePrResult(
            doc.RootElement.GetProperty("url").GetString(),
            doc.RootElement.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null,
            RemotePrStatus.Open,
            null);
    }

    public async Task<bool> MergePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.PutAsJsonAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/merge", new { });
        return resp.IsSuccessStatusCode;
    }

    public async Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.GetAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/issues/{prNumber}/comments");
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<RemotePrComment>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = new List<RemotePrComment>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new RemotePrComment(
                el.GetProperty("id").GetRawText(),
                el.GetProperty("body").GetString() ?? string.Empty,
                el.GetProperty("user").GetProperty("login").GetString() ?? string.Empty,
                el.GetProperty("created_at").GetDateTime()));
        }

        return list;
    }

    public async Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
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

    public Task UnregisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
        => Task.CompletedTask;

    public async Task<RemotePrStatus> GetPullRequestStatusAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.GetAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}");
        if (!resp.IsSuccessStatusCode)
            return RemotePrStatus.Open;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("merged", out var merged) && merged.GetBoolean())
            return RemotePrStatus.Merged;

        var state = doc.RootElement.GetProperty("state").GetString();
        return state == "closed" ? RemotePrStatus.Closed : RemotePrStatus.Open;
    }

    public async Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName)
    {
        ApplyHeaders(http, repo.Provider);
        var branchRef = Uri.EscapeDataString(branchName);
        using var resp = await http.DeleteAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/git/refs/heads/{branchRef}");
        return resp.IsSuccessStatusCode;
    }

    public Task<bool> CreatePullRequestCommentAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string body)
        => PrCommentHelper.CreatePullRequestCommentAsync(http, repo, prNumber, body, ApplyHeaders);

    public bool VerifyWebhookSignature(string body, IReadOnlyDictionary<string, string> headers, string secret)
        => VerifyHmacSha256(body, GetHeader(headers, "X-Hub-Signature-256"), secret);

    public WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers)
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

    private static void ApplyHeaders(HttpClient http, RemoteProvider provider)
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

    private static string NormalizeGitHubHost(string host)
        => host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : host;

    private static string BuildApiBase(Uri providerUri)
    {
        if (providerUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || providerUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return "https://api.github.com";

        var baseUrl = providerUri.ToString().TrimEnd('/');
        if (providerUri.AbsolutePath.TrimEnd('/').EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        return baseUrl + "/api/v3";
    }

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

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : null;

    private static bool VerifyHmacSha256(string body, string? signature, string? secret)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret))
            return false;

        var normalized = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature["sha256=".Length..]
            : signature;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expected));
    }
}