using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Forgejo / Gitea REST API client. Repo URL is expected to be
/// "https://host/owner/repo" or "https://host/owner/repo.git".
/// </summary>
public class RemoteProviderService : IRemoteProvider
{
    private readonly IProviderStore _providerStore;
    private readonly HttpClient _http;

    public RemoteProviderService(IProviderStore providerStore, HttpClient http)
    {
        _providerStore = providerStore;
        _http = http;
    }

    public async Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body)
    {
        var (apiBase, owner, repo) = await ResolveAsync(repoUrl);
        if (apiBase == null) return new RemotePrResult(null, null, RemotePrStatus.Open, "no provider configured");
        var endpoint = $"{apiBase}/repos/{owner}/{repo}/pulls";
        try
        {
            using var resp = await _http.PostAsJsonAsync(endpoint, new { title, body, head = sourceBranch, @base = targetBranch });
            if (!resp.IsSuccessStatusCode)
                return new RemotePrResult(null, null, RemotePrStatus.Open, $"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return new RemotePrResult(
                doc.RootElement.GetProperty("url").GetString(),
                doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                RemotePrStatus.Open, null);
        }
        catch (Exception ex)
        {
            return new RemotePrResult(null, null, RemotePrStatus.Open, ex.Message);
        }
    }

    public async Task<bool> MergePullRequestAsync(string repoUrl, string prNumber)
    {
        var (apiBase, owner, repo) = await ResolveAsync(repoUrl);
        if (apiBase == null) return false;
        try
        {
            using var resp = await _http.PostAsJsonAsync($"{apiBase}/repos/{owner}/{repo}/pulls/{prNumber}/merge", new { Do = "merge" });
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(string repoUrl, string prNumber)
    {
        var (apiBase, owner, repo) = await ResolveAsync(repoUrl);
        if (apiBase == null) return Array.Empty<RemotePrComment>();
        try
        {
            using var resp = await _http.GetAsync($"{apiBase}/repos/{owner}/{repo}/issues/{prNumber}/comments");
            if (!resp.IsSuccessStatusCode) return Array.Empty<RemotePrComment>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var list = new List<RemotePrComment>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new RemotePrComment(
                    el.GetProperty("id").GetRawText(),
                    el.GetProperty("body").GetString() ?? "",
                    el.GetProperty("user").GetProperty("login").GetString() ?? "",
                    el.GetProperty("created_at").GetDateTime()));
            }
            return list;
        }
        catch { return Array.Empty<RemotePrComment>(); }
    }

    public async Task RegisterWebhookAsync(string repoUrl, string callbackUrl)
    {
        var (apiBase, owner, repo) = await ResolveAsync(repoUrl);
        if (apiBase == null) return;
        try
        {
            await _http.PostAsJsonAsync($"{apiBase}/repos/{owner}/{repo}/hooks", new
            {
                type = "gitea",
                config = new { url = callbackUrl, content_type = "json" },
                events = new[] { "push", "pull_request", "pull_request_comment" },
                active = true,
            });
        }
        catch { }
    }

    public Task UnregisterWebhookAsync(string repoUrl, string callbackUrl) => Task.CompletedTask;

    public async Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber)
    {
        var (apiBase, owner, repo) = await ResolveAsync(repoUrl);
        if (apiBase == null) return RemotePrStatus.Open;
        try
        {
            using var resp = await _http.GetAsync($"{apiBase}/repos/{owner}/{repo}/pulls/{prNumber}");
            if (!resp.IsSuccessStatusCode) return RemotePrStatus.Open;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("merged", out var m) && m.GetBoolean()) return RemotePrStatus.Merged;
            var state = doc.RootElement.GetProperty("state").GetString();
            return state == "closed" ? RemotePrStatus.Closed : RemotePrStatus.Open;
        }
        catch { return RemotePrStatus.Open; }
    }

    public async Task<bool> DeleteBranchAsync(string repoUrl, string branchName)
    {
        var (apiBase, owner, repo) = await ResolveAsync(repoUrl);
        if (apiBase == null) return false;
        try
        {
            using var resp = await _http.DeleteAsync($"{apiBase}/repos/{owner}/{repo}/branches/{branchName}");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<(string? apiBase, string? owner, string? repo)> ResolveAsync(string repoUrl)
    {
        var providers = await _providerStore.GetAllRemoteProvidersAsync();
        var match = providers.FirstOrDefault(p => repoUrl.StartsWith(p.Url, StringComparison.OrdinalIgnoreCase));
        if (match == null) return (null, null, null);

        var path = repoUrl.Substring(match.Url.Length).TrimStart('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', 2);
        if (parts.Length < 2) return (null, null, null);

        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(match.ApiKey)
            ? null
            : new System.Net.Http.Headers.AuthenticationHeaderValue("token", match.ApiKey);

        var apiBase = match.Url.TrimEnd('/') + "/api/v1";
        return (apiBase, parts[0], parts[1]);
    }
}
