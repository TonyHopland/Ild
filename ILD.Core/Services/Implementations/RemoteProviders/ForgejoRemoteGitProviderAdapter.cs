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

    public override async Task<bool> EnablePullRequestAutoMergeAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        // Forgejo/Gitea schedule auto-merge through the same merge endpoint by
        // setting merge_when_checks_succeed; the PR merges once its checks pass.
        using var resp = await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/merge",
            new { Do = "merge", merge_when_checks_succeed = true });
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

    /// <summary>
    /// Forgejo keeps review comments under each review rather than in one
    /// collection of its own (verified against Forgejo 9.0.3 / Gitea 1.22.0:
    /// there is no <c>pulls/{index}/comments</c>), so the ledger costs one
    /// request per review on top of the reviews list and the pull request's own
    /// comments. A comment carries a <c>resolver</c> once someone has resolved
    /// it, which is how resolved state is reported here; there is no API to set
    /// it, so <see cref="ResolveReviewThreadAsync"/> stays refused.
    ///
    /// The nearest thing to a line number is the diff <c>position</c>, which is
    /// what the API offers; <c>original_commit_id</c> is the commit reviewed,
    /// as on GitHub. Suppressed findings are a Copilot artifact and simply do
    /// not occur here, so the review bodies yield none.
    /// </summary>
    public override async Task<RemotePrReviewLedger> GetPullRequestReviewLedgerAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        var apiRepo = $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}";

        using var prResp = await http.GetAsync($"{apiRepo}/pulls/{prNumber}");
        if (!prResp.IsSuccessStatusCode)
            return RemotePrReviewLedger.Unavailable(
                $"Could not read pull request #{prNumber} from {ProviderType} (HTTP {(int)prResp.StatusCode}).");
        using var prDoc = JsonDocument.Parse(await prResp.Content.ReadAsStringAsync());
        var headSha = prDoc.RootElement.TryGetProperty("head", out var head) ? ReadString(head, "sha") : null;

        var reviewPages = await ReadAllPagesAsync(http, $"{apiRepo}/pulls/{prNumber}/reviews");
        if (reviewPages is null)
            return RemotePrReviewLedger.Unavailable(
                $"Could not read the reviews on pull request #{prNumber} from {ProviderType}.");

        var reviews = new List<RemotePrReviewSummary>();
        var items = new List<RemotePrReviewItem>();
        foreach (var review in reviewPages)
        {
            var reviewId = ReadId(review) ?? string.Empty;
            reviews.Add(new RemotePrReviewSummary(
                reviewId,
                NormalizeReviewState(ReadString(review, "state")),
                ReadString(review, "body"),
                ReadString(review, "commit_id"),
                ReadDate(review, "submitted_at") ?? DateTime.MinValue,
                ReadUserLogin(review),
                Incomplete: false));

            if (reviewId.Length == 0) continue;
            var comments = await ReadAllPagesAsync(
                http, $"{apiRepo}/pulls/{prNumber}/reviews/{Uri.EscapeDataString(reviewId)}/comments");
            if (comments is null)
                return RemotePrReviewLedger.Unavailable(
                    $"Could not read the comments on review {reviewId} of pull request #{prNumber} from {ProviderType}.");

            foreach (var comment in comments)
            {
                var id = ReadId(comment);
                if (id is null) continue;
                var body = ReadString(comment, "body");
                items.Add(new RemotePrReviewItem(
                    "review",
                    id,
                    // No thread resource: a review is the nearest grouping there is.
                    reviewId,
                    reviewId,
                    ReadString(comment, "path"),
                    ReadInt(comment, "original_position") ?? ReadInt(comment, "position"),
                    Truncate(body, MaxReviewItemLength) ?? string.Empty,
                    ReadUserLogin(comment),
                    ReadString(comment, "original_commit_id") ?? ReadString(comment, "commit_id"),
                    ReadDate(comment, "created_at") ?? DateTime.MinValue,
                    Resolved: comment.TryGetProperty("resolver", out var resolver)
                        && resolver.ValueKind == JsonValueKind.Object
                        && !string.IsNullOrEmpty(ReadString(resolver, "login")),
                    PrCommentMarker.IsStamped(body)));
            }
        }

        var issueComments = await ReadAllPagesAsync(http, $"{apiRepo}/issues/{prNumber}/comments");
        if (issueComments is null)
            return RemotePrReviewLedger.Unavailable(
                $"Could not read the comments on pull request #{prNumber} from {ProviderType}.");

        foreach (var comment in issueComments)
        {
            var id = ReadId(comment);
            if (id is null) continue;
            var body = ReadString(comment, "body");
            items.Add(new RemotePrReviewItem(
                "issue", id, ThreadId: null, ReviewId: null, Path: null, Line: null,
                Truncate(body, MaxReviewItemLength) ?? string.Empty,
                ReadUserLogin(comment),
                headSha,
                ReadDate(comment, "created_at") ?? DateTime.MinValue,
                Resolved: false,
                PrCommentMarker.IsStamped(body)));
        }

        return new RemotePrReviewLedger(reviews, items, headSha, null);
    }

    /// <summary>
    /// Forgejo has no reply route on a review comment, so an answer is a fresh
    /// one-comment review anchored to the same file and line — which is where
    /// the reviewer raised it and where a reader looks for the answer.
    /// </summary>
    public override async Task<RemotePrWriteResult> ReplyToReviewThreadAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber, string commentId, string body)
    {
        var ledger = await GetPullRequestReviewLedgerAsync(http, repo, prNumber);
        if (ledger.Message is not null)
            return new RemotePrWriteResult(false, null, ledger.Message);

        var answered = ledger.Items.FirstOrDefault(i => string.Equals(i.CommentId, commentId, StringComparison.Ordinal));
        if (answered?.Path is null)
            return new RemotePrWriteResult(false, null,
                $"No review comment with id '{commentId}' on pull request #{prNumber}, so there is no file and line to answer on.");

        ApplyHeaders(http, repo.Provider);
        using var resp = await http.PostAsJsonAsync(
            $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/reviews",
            new
            {
                body = string.Empty,
                @event = "COMMENT",
                comments = new[] { new { path = answered.Path, body, new_position = answered.Line ?? 1 } },
            });
        if (!resp.IsSuccessStatusCode)
            return new RemotePrWriteResult(false, null, $"The reply was refused by {ProviderType} (HTTP {(int)resp.StatusCode}).");

        // The response is the REVIEW that was created, and its id lives in a
        // different sequence from the comment ids this adapter's ledger keys on
        // — recording it would eventually mark a real review comment as ILD's
        // own and swallow it. Read the one comment the review carries instead,
        // and hand back nothing rather than a wrong-space id if that read fails.
        var reviewId = await PrCommentHelper.ReadCreatedIdAsync(resp);
        return new RemotePrWriteResult(true, reviewId is null ? null : await ReadOnlyCommentIdAsync(http, repo, prNumber, reviewId), null);
    }

    /// <summary>The id of the single comment a just-created one-comment review holds.</summary>
    private static async Task<string?> ReadOnlyCommentIdAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber, string reviewId)
    {
        var comments = await GetArrayAsync(
            http, $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}/pulls/{prNumber}/reviews/{Uri.EscapeDataString(reviewId)}/comments");
        return comments.Count == 1 ? ReadId(comments[0]) : null;
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
