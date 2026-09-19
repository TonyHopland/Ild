using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// Shared helper for PR comment operations that use the same API endpoint
/// across both Forgejo and GitHub providers. Each adapter passes its own
/// header-application callback because auth schemes differ (token vs Bearer).
/// </summary>
internal static class PrCommentHelper
{
    public static async Task<RemotePrWriteResult> CreatePullRequestCommentAsync(
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
        if (!resp.IsSuccessStatusCode)
            return new RemotePrWriteResult(false, null, $"The comment was refused (HTTP {(int)resp.StatusCode}).");

        // The id is what stops the comment starting a round of its own when the
        // marker has been edited away; a response that does not name it costs
        // that second line of defence, not the post.
        return new RemotePrWriteResult(true, await ReadCreatedIdAsync(resp), null);
    }

    /// <summary>
    /// The id the provider gave whatever was just created, or null when the
    /// response did not name one. Best-effort by design: a write the provider
    /// accepted is a success whether or not it said what it called the result.
    /// </summary>
    internal static async Task<string?> ReadCreatedIdAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var id))
                return null;
            return id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString(),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }
}
