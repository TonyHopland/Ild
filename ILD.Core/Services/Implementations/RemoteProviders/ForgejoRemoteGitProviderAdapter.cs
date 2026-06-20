using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

public sealed class ForgejoRemoteGitProviderAdapter : RemoteGitProviderAdapterBase
{
    public override string ProviderType => "Forgejo";
    public override string WebhookRouteSegment => "forgejo";
    protected override string SignatureHeaderName => "X-Forgejo-Signature";

    protected override bool HostMatches(Uri providerUri, Uri repoUri)
        => providerUri.Host.Equals(repoUri.Host, StringComparison.OrdinalIgnoreCase)
            && providerUri.Scheme.Equals(repoUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && providerUri.Port == repoUri.Port;

    protected override string BuildApiBase(Uri providerUri)
        => providerUri.ToString().TrimEnd('/') + "/api/v1";

    // Forgejo/Gitea reports a changes-requested review as "REQUEST_CHANGES";
    // map it onto GitHub's "CHANGES_REQUESTED" so the snapshot logic is uniform.
    protected override string NormalizeReviewState(string? state)
    {
        var normalized = base.NormalizeReviewState(state);
        return normalized == "REQUEST_CHANGES" ? "CHANGES_REQUESTED" : normalized;
    }

    public override async Task<bool> MergePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        using var resp = await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/merge",
            new { Do = "merge" });
        return resp.IsSuccessStatusCode;
    }

    public override async Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
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

    public override async Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName)
    {
        ApplyHeaders(http, repo.Provider);
        var branchRef = Uri.EscapeDataString(branchName);
        using var resp = await http.DeleteAsync($"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/branches/{branchRef}");
        return resp.IsSuccessStatusCode;
    }

    public override WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers)
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

    protected override void ApplyHeaders(HttpClient http, RemoteProvider provider)
    {
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.UserAgent.Clear();

        if (!string.IsNullOrEmpty(provider.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", provider.ApiKey);
        }
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
}
