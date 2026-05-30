using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// Shared scaffolding for HTTP-API remote git providers (GitHub, Forgejo).
/// The two providers expose the same REST shapes for the bulk of operations —
/// PR creation, comment/status reads, HMAC webhook verification — and only
/// diverge on auth headers, host/api-base resolution, the merge/branch/webhook
/// endpoints, and webhook payload parsing. The identical parts (notably the
/// security-sensitive HMAC check) live here so they cannot drift between
/// providers; the divergent parts are abstract hooks.
/// </summary>
public abstract class RemoteGitProviderAdapterBase : IRemoteGitProviderAdapter
{
    public abstract string ProviderType { get; }
    public abstract string WebhookRouteSegment { get; }

    /// <summary>Header carrying the HMAC-SHA256 webhook signature for this provider.</summary>
    protected abstract string SignatureHeaderName { get; }

    /// <summary>Apply auth/accept/user-agent headers for this provider's API.</summary>
    protected abstract void ApplyHeaders(HttpClient http, RemoteProvider provider);

    /// <summary>Whether a configured provider URL serves the given repo URL.</summary>
    protected abstract bool HostMatches(Uri providerUri, Uri repoUri);

    /// <summary>Build the REST API base URL for a provider instance.</summary>
    protected abstract string BuildApiBase(Uri providerUri);

    public ResolvedRemoteRepository? TryResolve(RemoteProvider provider, Uri repoUri)
    {
        if (!string.Equals(provider.Type, ProviderType, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(provider.Url, UriKind.Absolute, out var providerUri))
            return null;

        if (!HostMatches(providerUri, repoUri))
            return null;

        var path = repoUri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', 2);
        if (parts.Length < 2)
            return null;

        return new ResolvedRemoteRepository(provider, ProviderType, BuildApiBase(providerUri), parts[0], parts[1], this);
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

    public async Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.GetAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/issues/{prNumber}/comments");
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<RemotePrComment>();

        return await ReadCommentsAsync(resp);
    }

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

    public Task UnregisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
        => Task.CompletedTask;

    public Task<bool> CreatePullRequestCommentAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string body)
        => PrCommentHelper.CreatePullRequestCommentAsync(http, repo, prNumber, body, ApplyHeaders);

    public bool VerifyWebhookSignature(string body, IReadOnlyDictionary<string, string> headers, string secret)
        => VerifyHmacSha256(body, GetHeader(headers, SignatureHeaderName), secret);

    public abstract Task<bool> MergePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    public abstract Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl);
    public abstract Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName);
    public abstract WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers);

    protected static async Task<IEnumerable<RemotePrComment>> ReadCommentsAsync(HttpResponseMessage resp)
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

    protected static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : null;

    protected static bool VerifyHmacSha256(string body, string? signature, string? secret)
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
