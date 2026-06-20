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

    /// <summary>
    /// How many times <see cref="GetPullRequestSnapshotAsync"/> re-fetches the PR
    /// while its <c>mergeable</c> field is still null. GitHub computes
    /// mergeability asynchronously after a push and reports null in the meantime.
    /// </summary>
    protected virtual int MergeableRetryAttempts => 3;

    /// <summary>Delay between mergeable-unknown retries.</summary>
    protected virtual TimeSpan MergeableRetryDelay => TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Normalise a provider's review-state string to GitHub's vocabulary
    /// (<c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>DISMISSED</c>, …). GitHub
    /// already uses these; other providers override.
    /// </summary>
    protected virtual string NormalizeReviewState(string? state)
        => (state ?? string.Empty).ToUpperInvariant();

    public virtual async Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        var apiRepo = $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Repo}";

        JsonElement pr = default;
        var gotPr = false;
        for (var attempt = 0; attempt < Math.Max(1, MergeableRetryAttempts); attempt++)
        {
            using var resp = await http.GetAsync($"{apiRepo}/pulls/{prNumber}");
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            pr = doc.RootElement.Clone();
            gotPr = true;

            var closed = string.Equals(ReadString(pr, "state"), "closed", StringComparison.OrdinalIgnoreCase);
            var mergeableKnown = pr.TryGetProperty("mergeable", out var m) && m.ValueKind != JsonValueKind.Null;
            if (closed || mergeableKnown || attempt == Math.Max(1, MergeableRetryAttempts) - 1)
                break;
            await Task.Delay(MergeableRetryDelay);
        }
        if (!gotPr)
            return null;

        var state = ReadString(pr, "state") ?? "open";
        var merged = pr.TryGetProperty("merged", out var mergedEl) && mergedEl.ValueKind == JsonValueKind.True;
        bool? mergeable = pr.TryGetProperty("mergeable", out var mergeableEl)
            ? mergeableEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            }
            : null;
        var mergeableState = ReadString(pr, "mergeable_state");
        var headSha = pr.TryGetProperty("head", out var head) ? ReadString(head, "sha") : null;

        var (approved, changesRequested, reviewEntries) = await ReadReviewsAsync(http, apiRepo, prNumber);
        var ci = headSha is null ? RemotePrCiStatus.None : await ReadCiStatusAsync(http, apiRepo, headSha);
        var conversation = await ReadConversationAsync(http, apiRepo, prNumber, reviewEntries);

        return new RemotePrSnapshot(
            ReadString(pr, "title"),
            ReadString(pr, "body"),
            state,
            merged,
            mergeable,
            mergeableState,
            ci,
            approved,
            changesRequested,
            conversation,
            DateTime.UtcNow);
    }

    private async Task<(bool Approved, bool ChangesRequested, List<RemotePrConversationEntry> Reviews)> ReadReviewsAsync(
        HttpClient http, string apiRepo, string prNumber)
    {
        var reviews = await GetArrayAsync(http, $"{apiRepo}/pulls/{prNumber}/reviews");
        var entries = new List<RemotePrConversationEntry>();
        // A reviewer's stance is their latest *decisive* review (APPROVED /
        // CHANGES_REQUESTED / DISMISSED). COMMENTED and PENDING reviews never
        // change the decision — a comment posted after an approval must NOT drop
        // that approval — and a since-DISMISSED approval no longer counts.
        var latestByUser = new Dictionary<string, (DateTime At, string State)>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in reviews)
        {
            var author = ReadUserLogin(review);
            var normalized = NormalizeReviewState(ReadString(review, "state"));
            var at = ReadDate(review, "submitted_at") ?? DateTime.MinValue;
            if (normalized is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED"
                && (!latestByUser.TryGetValue(author, out var existing) || at >= existing.At))
                latestByUser[author] = (at, normalized);

            var body = ReadString(review, "body") ?? string.Empty;
            // Keep verdict reviews even when empty so the UI shows approve/reject
            // events; drop content-free "commented" reviews.
            if (!string.IsNullOrWhiteSpace(body) || normalized is "APPROVED" or "CHANGES_REQUESTED")
                entries.Add(new RemotePrConversationEntry("review", author, body, at, normalized));
        }

        var approved = latestByUser.Values.Any(v => v.State == "APPROVED");
        var changesRequested = latestByUser.Values.Any(v => v.State == "CHANGES_REQUESTED");
        return (approved, changesRequested, entries);
    }

    private async Task<RemotePrCiStatus> ReadCiStatusAsync(HttpClient http, string apiRepo, string headSha)
    {
        var sha = Uri.EscapeDataString(headSha);
        var anyPresent = false;
        var anyFailed = false;
        var anyPending = false;

        foreach (var run in await GetArrayAsync(http, $"{apiRepo}/commits/{sha}/check-runs", "check_runs"))
        {
            anyPresent = true;
            var status = (ReadString(run, "status") ?? string.Empty).ToLowerInvariant();
            var conclusion = (ReadString(run, "conclusion") ?? string.Empty).ToLowerInvariant();
            if (status != "completed")
                anyPending = true;
            else if (conclusion is "failure" or "timed_out" or "cancelled")
                anyFailed = true;
            else if (conclusion is not ("success" or "neutral" or "skipped"))
                anyPending = true;
        }

        // Combined commit status (the legacy statuses API): state is the rollup
        // of all contexts (success | pending | failure | error).
        var combined = await GetObjectAsync(http, $"{apiRepo}/commits/{sha}/status");
        if (combined.HasValue
            && combined.Value.TryGetProperty("statuses", out var statuses)
            && statuses.ValueKind == JsonValueKind.Array
            && statuses.GetArrayLength() > 0)
        {
            anyPresent = true;
            var rollup = (ReadString(combined.Value, "state") ?? string.Empty).ToLowerInvariant();
            if (rollup is "failure" or "error")
                anyFailed = true;
            else if (rollup == "pending")
                anyPending = true;
        }

        if (anyFailed) return RemotePrCiStatus.Failed;
        if (!anyPresent) return RemotePrCiStatus.None;
        return anyPending ? RemotePrCiStatus.Pending : RemotePrCiStatus.Passed;
    }

    private async Task<IReadOnlyList<RemotePrConversationEntry>> ReadConversationAsync(
        HttpClient http, string apiRepo, string prNumber, List<RemotePrConversationEntry> reviews)
    {
        var conversation = new List<RemotePrConversationEntry>(reviews);

        foreach (var c in await GetArrayAsync(http, $"{apiRepo}/issues/{prNumber}/comments"))
            conversation.Add(new RemotePrConversationEntry(
                "comment", ReadUserLogin(c), ReadString(c, "body") ?? string.Empty, ReadDate(c, "created_at") ?? DateTime.MinValue, null));

        foreach (var c in await GetArrayAsync(http, $"{apiRepo}/pulls/{prNumber}/comments"))
            conversation.Add(new RemotePrConversationEntry(
                "review_comment", ReadUserLogin(c), ReadString(c, "body") ?? string.Empty, ReadDate(c, "created_at") ?? DateTime.MinValue, null));

        return conversation.OrderBy(e => e.CreatedAt).ToList();
    }

    /// <summary>GET a JSON array, returning cloned elements; empty on any failure (404, non-array).</summary>
    private static async Task<IReadOnlyList<JsonElement>> GetArrayAsync(HttpClient http, string url, string? property = null)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<JsonElement>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (property is not null)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out root))
                    return Array.Empty<JsonElement>();
            }
            if (root.ValueKind != JsonValueKind.Array)
                return Array.Empty<JsonElement>();
            return root.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch
        {
            return Array.Empty<JsonElement>();
        }
    }

    /// <summary>GET a JSON object, returning a cloned element; null on any failure.</summary>
    private static async Task<JsonElement?> GetObjectAsync(HttpClient http, string url)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadUserLogin(JsonElement element)
        => element.TryGetProperty("user", out var user) ? ReadString(user, "login") ?? string.Empty : string.Empty;

    private static DateTime? ReadDate(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var dt)
            ? dt
            : null;

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
