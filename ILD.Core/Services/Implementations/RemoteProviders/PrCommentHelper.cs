using System.Net.Http;
using System.Net.Http.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// Shared helper for PR comment operations that use the same API endpoint
/// across both Forgejo and GitHub providers. Each adapter passes its own
/// header-application callback because auth schemes differ (token vs Bearer).
/// </summary>
internal static class PrCommentHelper
{
    public static async Task<bool> CreatePullRequestCommentAsync(
        HttpClient http,
        ResolvedRemoteRepository repo,
        string prNumber,
        string body,
        Action<HttpClient, RemoteProvider> applyHeaders)
    {
        applyHeaders(http, repo.Provider);

        using var resp = await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/issues/{prNumber}/comments",
            new { body });
        return resp.IsSuccessStatusCode;
    }
}
