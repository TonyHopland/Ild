using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

public sealed class ForgejoRemoteGitProviderAdapter : IRemoteGitProviderAdapter
{
    public string ProviderType => "Forgejo";
    public string WebhookRouteSegment => "forgejo";

    public ResolvedRemoteRepository? TryResolve(RemoteProvider provider, Uri repoUri)
    {
        if (!string.Equals(provider.Type, ProviderType, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(provider.Url, UriKind.Absolute, out var providerUri))
            return null;

        if (!providerUri.Host.Equals(repoUri.Host, StringComparison.OrdinalIgnoreCase)
            || !providerUri.Scheme.Equals(repoUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || providerUri.Port != repoUri.Port)
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
            providerUri.ToString().TrimEnd('/') + "/api/v1",
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
        using var resp = await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/merge",
            new { Do = "merge" });
        return resp.IsSuccessStatusCode;
    }

    public async Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.GetAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/issues/{prNumber}/comments");
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<RemotePrComment>();

        return await ReadCommentsAsync(resp);
    }

    public async Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
    {
        ApplyHeaders(http, repo.Provider);
        await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/hooks",
            new
            {
                type = "gitea",
                config = new { url = callbackUrl, content_type = "json" },
                events = new[] { "push", "pull_request", "pull_request_comment" },
                active = true,
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

        return await ReadStatusAsync(resp);
    }

    public async Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName)
    {
        ApplyHeaders(http, repo.Provider);
        var branchRef = Uri.EscapeDataString(branchName);
        using var resp = await http.DeleteAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/branches/{branchRef}");
        return resp.IsSuccessStatusCode;
    }

    public Task<bool> CreatePullRequestCommentAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string body)
        => PrCommentHelper.CreatePullRequestCommentAsync(http, repo, prNumber, body, ApplyHeaders);

    public bool VerifyWebhookSignature(string body, IReadOnlyDictionary<string, string> headers, string secret)
        => VerifyHmacSha256(body, GetHeader(headers, "X-Forgejo-Signature"), secret);

    public WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers)
    {
        var normalized = TryParseNormalizedPayload(body);
        if (normalized != null)
            return normalized;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var repoId = GetRepositoryId(root);
        var pr = TryGetProperty(root, "pull_request");
        var comment = TryGetProperty(root, "comment");
        var review = TryGetProperty(root, "review");
        var action = TryGetString(root, "action");

        if (pr.HasValue)
        {
            var prId = TryGetString(pr.Value, "number");
            var prUrl = TryGetString(pr.Value, "html_url") ?? TryGetString(pr.Value, "url");

            if (string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase))
            {
                var merged = TryGetBool(pr.Value, "merged");
                return new WebhookPayload(
                    merged ? "pull_request.merged" : "pull_request.rejected",
                    repoId,
                    prId,
                    prUrl,
                    null,
                    merged ? "merged" : "closed");
            }

            if (comment.HasValue)
            {
                return new WebhookPayload(
                    "pull_request.comment",
                    repoId,
                    prId,
                    prUrl,
                    TryGetString(comment.Value, "body"),
                    null);
            }

            if (review.HasValue && string.Equals(TryGetString(review.Value, "state"), "changes_requested", StringComparison.OrdinalIgnoreCase))
            {
                return new WebhookPayload(
                    "pull_request.rejected",
                    repoId,
                    prId,
                    prUrl,
                    TryGetString(review.Value, "body"),
                    "changes_requested");
            }
        }

        return null;
    }

    private static void ApplyHeaders(HttpClient http, RemoteProvider provider)
    {
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.UserAgent.Clear();

        if (!string.IsNullOrEmpty(provider.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", provider.ApiKey);
        }
    }

    private static async Task<IEnumerable<RemotePrComment>> ReadCommentsAsync(HttpResponseMessage resp)
    {
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

    private static async Task<RemotePrStatus> ReadStatusAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("merged", out var merged) && merged.GetBoolean())
            return RemotePrStatus.Merged;

        var state = doc.RootElement.GetProperty("state").GetString();
        return state == "closed" ? RemotePrStatus.Closed : RemotePrStatus.Open;
    }

    private static WebhookPayload? TryParseNormalizedPayload(string body)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<WebhookPayload>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            return string.IsNullOrWhiteSpace(payload?.EventType) || string.IsNullOrWhiteSpace(payload.RepositoryId)
                ? null
                : payload;
        }
        catch
        {
            return null;
        }
    }

    private static string GetRepositoryId(JsonElement root)
        => TryGetString(root, "repositoryId")
            ?? TryGetString(TryGetProperty(root, "repository"), "id")
            ?? TryGetString(TryGetProperty(root, "repository"), "full_name")
            ?? TryGetString(TryGetProperty(root, "repository"), "name")
            ?? "unknown";

    private static JsonElement? TryGetProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value)
            ? value
            : null;

    private static string? TryGetString(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static bool TryGetBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

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