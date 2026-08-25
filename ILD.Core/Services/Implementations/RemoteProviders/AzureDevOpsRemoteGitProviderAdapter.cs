using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// Azure DevOps Services / Server. Shares only the scaffolding of
/// <see cref="RemoteGitProviderAdapterBase"/> — every REST call, the auth scheme
/// and the webhook check are its own, because Azure DevOps is not Gitea-shaped:
/// repositories live under an organisation *and* a project, the API is versioned
/// per request, PR conversation is threads rather than issue comments, and its
/// service hooks carry no signature at all.
/// </summary>
public sealed class AzureDevOpsRemoteGitProviderAdapter : RemoteGitProviderAdapterBase
{
    /// <summary>
    /// Every Azure DevOps request must name an API version or the service
    /// answers 400; one constant so the whole adapter moves together.
    /// </summary>
    private const string ApiVersion = "7.1";

    private const string GitPathSegment = "_git";
    private const string LegacyHostSuffix = ".visualstudio.com";
    private const string ModernHost = "dev.azure.com";
    private const string DeletedObjectId = "0000000000000000000000000000000000000000";

    /// <summary>
    /// Azure DevOps identifies the branch-policy build by a fixed type id;
    /// its display name is localised and cannot be matched on.
    /// </summary>
    private const string BuildPolicyTypeId = "0609b952-1397-4640-95ec-e00a01b2c241";

    /// <summary>
    /// The username half of the Basic credential a service hook is registered
    /// with. Azure DevOps has no per-subscription identity, so only the password
    /// half is a secret and only it is verified.
    /// </summary>
    private const string WebhookBasicAuthUsername = "ild";

    private static readonly string[] SubscribedEventTypes =
    {
        "git.pullrequest.merged",
        "git.pullrequest.updated",
        "ms.vss-code.git-pullrequest-comment-event",
    };

    public override string ProviderType => "AzureDevOps";
    public override string WebhookRouteSegment => "azuredevops";
    protected override string SignatureHeaderName => "Authorization";

    protected override bool HostMatches(Uri providerUri, Uri repoUri)
        => IsAzureDevOpsHost(repoUri.Host)
            && providerUri.Host.Equals(repoUri.Host, StringComparison.OrdinalIgnoreCase)
            && providerUri.Scheme.Equals(repoUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && providerUri.Port == repoUri.Port;

    protected override string BuildApiBase(Uri providerUri)
        => $"{providerUri.Scheme}://{providerUri.Authority}";

    /// <summary>
    /// Azure DevOps repository URLs are
    /// <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c>, or the legacy
    /// <c>https://{org}.visualstudio.com/[{collection}/]{project}/_git/{repo}</c>
    /// where the organisation is the hostname instead. The base class's
    /// owner/repo split cannot express that, so the project (and any collection
    /// above it) is folded into <see cref="ResolvedRemoteRepository.ApiBase"/>,
    /// which becomes the project-rooted API root every call here hangs off;
    /// <c>Owner</c> stays the organisation and <c>Repo</c> the repository.
    /// </summary>
    public override ResolvedRemoteRepository? TryResolve(RemoteProvider provider, Uri repoUri)
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

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gitAt = Array.FindIndex(segments, s => s.Equals(GitPathSegment, StringComparison.OrdinalIgnoreCase));
        if (gitAt < 0 || gitAt == segments.Length - 1)
            return null;

        var scope = segments[..gitAt];
        var legacyOrganization = LegacyOrganization(repoUri.Host);
        if (scope.Length < (legacyOrganization is null ? 2 : 1))
            return null;

        var organization = legacyOrganization ?? Uri.UnescapeDataString(scope[0]);
        if (legacyOrganization is null && !OrganizationAllowed(providerUri, organization))
            return null;

        return new ResolvedRemoteRepository(
            provider,
            ProviderType,
            $"{repoUri.Scheme}://{repoUri.Authority}/{string.Join('/', scope)}",
            organization,
            Uri.UnescapeDataString(segments[gitAt + 1]),
            this);
    }

    public override async Task<RemotePrResult> CreatePullRequestAsync(
        HttpClient http, ResolvedRemoteRepository repo, string sourceBranch, string targetBranch, string title, string body)
    {
        ApplyHeaders(http, repo.Provider);

        using var resp = await http.PostAsJsonAsync(
            Versioned($"{GitApi(repo)}/pullrequests"),
            new
            {
                sourceRefName = RefName(sourceBranch),
                targetRefName = RefName(targetBranch),
                title,
                description = body,
            });

        if (!resp.IsSuccessStatusCode)
            return new RemotePrResult(null, null, RemotePrStatus.Open, $"HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var prNumber = ReadScalar(doc.RootElement, "pullRequestId");
        if (prNumber is null)
            return new RemotePrResult(null, null, RemotePrStatus.Open, "the created pull request carried no id");

        return new RemotePrResult(PrApi(repo, prNumber), WebUrl(repo, prNumber), RemotePrStatus.Open, null);
    }

    public override async Task<bool> MergePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);

        // Completing a PR echoes back the source commit the caller last saw, so
        // Azure DevOps refuses a completion that would race a fresh push.
        var pr = await GetObjectAsync(http, Versioned(PrApi(repo, prNumber)));
        var lastCommit = ReadString(Child(pr, "lastMergeSourceCommit"), "commitId");
        if (lastCommit is null)
            return false;

        using var resp = await http.PatchAsJsonAsync(
            Versioned(PrApi(repo, prNumber)),
            new { status = "completed", lastMergeSourceCommit = new { commitId = lastCommit } });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Azure DevOps auto-complete: the PR records the identity that armed it and
    /// accepts only the caller's own, which a PAT does not name — so the
    /// organisation's connection data is asked who the token belongs to first.
    /// </summary>
    public override async Task<bool> EnablePullRequestAutoMergeAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);

        var connection = await GetObjectAsync(http, Versioned($"{OrganizationApi(repo)}/_apis/connectionData"));
        var identity = ReadString(Child(connection, "authenticatedUser"), "id");
        if (identity is null)
            return false;

        using var resp = await http.PatchAsJsonAsync(
            Versioned(PrApi(repo, prNumber)),
            new { autoCompleteSetBy = new { id = identity } });
        return resp.IsSuccessStatusCode;
    }

    public override async Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        return (await ReadThreadCommentsAsync(http, repo, prNumber))
            .Select(c => new RemotePrComment(c.Id, c.Body, c.Author, c.At))
            .ToList();
    }

    public override async Task<bool> CreatePullRequestCommentAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber, string body)
    {
        ApplyHeaders(http, repo.Provider);

        // A standalone PR comment is a new thread with one comment in it; there
        // is no comment endpoint of the shape PrCommentHelper posts to.
        using var resp = await http.PostAsJsonAsync(
            Versioned($"{PrApi(repo, prNumber)}/threads"),
            new
            {
                comments = new[] { new { parentCommentId = 0, content = body, commentType = "text" } },
                status = "active",
            });
        return resp.IsSuccessStatusCode;
    }

    public override async Task<RemotePrStatus> GetPullRequestStatusAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);
        var pr = await GetObjectAsync(http, Versioned(PrApi(repo, prNumber)));
        if (pr is null)
            return RemotePrStatus.Open;

        return (ReadString(pr, "status") ?? string.Empty).ToLowerInvariant() switch
        {
            "completed" => RemotePrStatus.Merged,
            "abandoned" => RemotePrStatus.Closed,
            _ => RemotePrStatus.Open,
        };
    }

    public override async Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        ApplyHeaders(http, repo.Provider);

        var pr = await GetObjectAsync(http, Versioned(PrApi(repo, prNumber)));
        if (pr is null)
            return null;

        var status = (ReadString(pr, "status") ?? "active").ToLowerInvariant();
        var merged = status == "completed";
        var closed = merged || status == "abandoned";

        var mergeStatus = (ReadString(pr, "mergeStatus") ?? string.Empty).ToLowerInvariant();
        bool? mergeable = mergeStatus switch
        {
            "succeeded" => true,
            // "rejectedByPolicy" is deliberately not false: a policy the PR has
            // not satisfied is not the branch conflict that reading holds.
            "conflicts" or "failure" => false,
            _ => null,
        };

        var (approved, changesRequested, reviews) = ReadReviewers(pr.Value);
        var conversation = await ReadConversationAsync(http, repo, prNumber, reviews);
        var (ci, failedChecks) = await ReadCiStatusAsync(http, repo, pr.Value, prNumber);

        return new RemotePrSnapshot(
            ReadString(pr, "title"),
            ReadString(pr, "description"),
            closed ? "closed" : "open",
            merged,
            mergeable,
            // The conflict edge keys on GitHub's vocabulary, so the one Azure
            // DevOps merge status that means the same thing is spelled its way.
            mergeStatus == "conflicts" ? "dirty" : (mergeStatus.Length == 0 ? null : mergeStatus),
            ci,
            failedChecks,
            approved,
            changesRequested,
            conversation,
            DateTime.UtcNow);
    }

    /// <summary>
    /// The log of the build behind a failing branch policy. Azure DevOps keeps a
    /// build's output as one log per timeline step rather than a single stream,
    /// so this picks the log of the step that actually failed — the last one —
    /// and falls back to the build's final log when the timeline says nothing.
    /// </summary>
    public override async Task<RemoteCiLog> GetCheckLogAsync(
        HttpClient http, ResolvedRemoteRepository repo, string checkId, int tailLines, int offset)
    {
        ApplyHeaders(http, repo.Provider);

        var build = $"{repo.ApiBase}/_apis/build/builds/{Uri.EscapeDataString(checkId)}";
        var logId = await ResolveFailedLogIdAsync(http, build);
        if (logId is null)
            return RemoteCiLog.Unavailable(
                "No log for this check — Azure DevOps keeps build logs for a limited time, and a check published by something other than Azure Pipelines has none to fetch.");

        using var resp = await http.GetAsync(
            Versioned($"{build}/logs/{logId}"),
            HttpCompletionOption.ResponseHeadersRead);

        if (!resp.IsSuccessStatusCode)
            return RemoteCiLog.Unavailable($"Could not read the log for this check (HTTP {(int)resp.StatusCode}).");

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var window = await WindowAsync(stream, tailLines, offset);
        return window.TotalLines == 0
            ? RemoteCiLog.Unavailable("The log for this check is empty.")
            : window;
    }

    public override async Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl)
    {
        // The Basic password IS the verification, so a subscription registered
        // without a secret is one whose deliveries can never be admitted.
        if (string.IsNullOrEmpty(repo.Provider.WebhookSecret))
            return;

        ApplyHeaders(http, repo.Provider);

        // Service-hook subscriptions are keyed by the repository's and project's
        // ids, not their names, and are created at organisation scope.
        var git = await GetObjectAsync(http, Versioned(GitApi(repo)));
        var repositoryId = ReadString(git, "id");
        var projectId = ReadString(Child(git, "project"), "id");
        if (repositoryId is null || projectId is null)
            return;

        foreach (var eventType in SubscribedEventTypes)
        {
            using var response = await http.PostAsJsonAsync(
                Versioned($"{OrganizationApi(repo)}/_apis/hooks/subscriptions"),
                new
                {
                    publisherId = "tfs",
                    eventType,
                    resourceVersion = "1.0",
                    consumerId = "webHooks",
                    consumerActionId = "httpRequest",
                    publisherInputs = new { projectId, repository = repositoryId },
                    consumerInputs = new
                    {
                        url = callbackUrl,
                        basicAuthUsername = WebhookBasicAuthUsername,
                        basicAuthPassword = repo.Provider.WebhookSecret,
                    },
                });
        }
    }

    public override async Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName)
    {
        ApplyHeaders(http, repo.Provider);

        // A ref is deleted by updating it to the null object id, which means the
        // caller has to know what it currently points at.
        var refName = RefName(branchName);
        var refs = await GetValuesAsync(http, Versioned(
            $"{GitApi(repo)}/refs", $"filter={Uri.EscapeDataString(refName["refs/".Length..])}"));
        var objectId = refs
            .Where(r => string.Equals(ReadString(r, "name"), refName, StringComparison.Ordinal))
            .Select(r => ReadString(r, "objectId"))
            .FirstOrDefault(id => id is not null);
        if (objectId is null)
            return false;

        using var resp = await http.PostAsJsonAsync(
            Versioned($"{GitApi(repo)}/refs"),
            new[] { new { name = refName, oldObjectId = objectId, newObjectId = DeletedObjectId } });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Azure DevOps service hooks carry no signature of any kind; a subscription
    /// authenticates itself with the Basic credential it was registered with, so
    /// that credential's password half <b>is</b> the webhook secret. Absent
    /// header, non-Basic header, undecodable credential or unset secret all fail
    /// closed, and the comparison is fixed-time exactly as the HMAC path is.
    /// </summary>
    public override bool VerifyWebhookSignature(string body, IReadOnlyDictionary<string, string> headers, string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return false;

        var header = GetHeader(headers, SignatureHeaderName);
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        string credential;
        try
        {
            credential = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = credential.IndexOf(':');
        if (separator < 0)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(credential[(separator + 1)..]),
            Encoding.UTF8.GetBytes(secret));
    }

    public override WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var eventType = ReadString(root, "eventType") ?? string.Empty;
        var resource = Child(root, "resource");
        if (resource is null)
            return null;

        // The comment event nests the pull request; the lifecycle events are the
        // pull request.
        var isComment = eventType.Contains("pullrequest-comment", StringComparison.OrdinalIgnoreCase);
        var pr = isComment ? Child(resource.Value, "pullRequest") : resource;
        if (pr is null)
            return null;

        var repositoryId = ReadString(Child(pr.Value, "repository"), "id")
            ?? ReadString(Child(pr.Value, "repository"), "name")
            ?? "unknown";
        var prNumber = ReadScalar(pr.Value, "pullRequestId");
        var prUrl = WebUrlFromPayload(pr.Value, prNumber);

        if (isComment)
        {
            var content = ReadString(Child(resource.Value, "comment"), "content");
            return string.IsNullOrWhiteSpace(content)
                ? null
                : new WebhookPayload("pull_request.comment", repositoryId, prNumber, prUrl, content, null);
        }

        var status = (ReadString(pr.Value, "status") ?? string.Empty).ToLowerInvariant();
        if (status == "completed")
            return new WebhookPayload("pull_request.merged", repositoryId, prNumber, prUrl, null, "merged");
        if (status == "abandoned")
            return new WebhookPayload("pull_request.rejected", repositoryId, prNumber, prUrl, null, "closed");

        // An open PR only reaches an edge through a reviewer having voted it
        // down; every other update is a refresh the poller already covers.
        var (_, changesRequested, _) = ReadReviewers(pr.Value);
        return changesRequested
            ? new WebhookPayload("pull_request.rejected", repositoryId, prNumber, prUrl, null, "changes_requested")
            : null;
    }

    /// <summary>
    /// Azure DevOps authenticates a PAT as Basic with an empty username and the
    /// token as the password — not as a bearer token, which it rejects.
    /// </summary>
    protected override void ApplyHeaders(HttpClient http, RemoteProvider provider)
    {
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.UserAgent.Clear();

        if (!string.IsNullOrEmpty(provider.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($":{provider.ApiKey}")));
        }

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ILD", "1.0"));
    }

    private static bool IsAzureDevOpsHost(string host)
        => host.Equals(ModernHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(LegacyHostSuffix, StringComparison.OrdinalIgnoreCase);

    private static string? LegacyOrganization(string host)
        => host.EndsWith(LegacyHostSuffix, StringComparison.OrdinalIgnoreCase)
            ? host[..^LegacyHostSuffix.Length]
            : null;

    /// <summary>
    /// dev.azure.com serves every organisation from the one host, so a provider
    /// URL that names one (<c>https://dev.azure.com/contoso</c>) is read as an
    /// organisation constraint — PATs are organisation-scoped, so two
    /// organisations are two configured providers rather than one.
    /// </summary>
    private static bool OrganizationAllowed(Uri providerUri, string organization)
    {
        var configured = providerUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return configured.Length == 0
            || Uri.UnescapeDataString(configured[0]).Equals(organization, StringComparison.OrdinalIgnoreCase);
    }

    private static string GitApi(ResolvedRemoteRepository repo)
        => $"{repo.ApiBase}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repo)}";

    private static string PrApi(ResolvedRemoteRepository repo, string prNumber)
        => $"{GitApi(repo)}/pullrequests/{Uri.EscapeDataString(prNumber)}";

    /// <summary>The organisation (or, on a legacy collection URL, the collection) root above the project.</summary>
    private static string OrganizationApi(ResolvedRemoteRepository repo)
        => repo.ApiBase[..repo.ApiBase.LastIndexOf('/')];

    private static string WebUrl(ResolvedRemoteRepository repo, string prNumber)
        => $"{repo.ApiBase}/_git/{Uri.EscapeDataString(repo.Repo)}/pullrequest/{prNumber}";

    private static string Versioned(string url, string? query = null)
        => query is null ? $"{url}?api-version={ApiVersion}" : $"{url}?{query}&api-version={ApiVersion}";

    private static string RefName(string branch)
        => branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";

    private static string? WebUrlFromPayload(JsonElement pr, string? prNumber)
    {
        var repoWebUrl = ReadString(Child(pr, "repository"), "webUrl");
        if (repoWebUrl is not null && prNumber is not null)
            return $"{repoWebUrl.TrimEnd('/')}/pullrequest/{prNumber}";

        return ReadString(Child(Child(pr, "_links"), "web"), "href") ?? ReadString(pr, "url");
    }

    /// <summary>
    /// A reviewer's stance is their vote: Azure DevOps scores approval positive
    /// (10 approved, 5 approved with suggestions) and rejection negative (-5
    /// waiting for the author, -10 rejected), with 0 meaning no opinion yet.
    /// Votes carry no body or timestamp, so the conversation entries exist to
    /// show that a verdict happened, not to say anything about it.
    /// </summary>
    private static (bool Approved, bool ChangesRequested, List<RemotePrConversationEntry> Reviews) ReadReviewers(JsonElement pr)
    {
        var entries = new List<RemotePrConversationEntry>();
        var approved = false;
        var changesRequested = false;

        if (!pr.TryGetProperty("reviewers", out var reviewers) || reviewers.ValueKind != JsonValueKind.Array)
            return (approved, changesRequested, entries);

        foreach (var reviewer in reviewers.EnumerateArray())
        {
            if (!reviewer.TryGetProperty("vote", out var voteElement) || voteElement.ValueKind != JsonValueKind.Number)
                continue;

            var vote = voteElement.GetDouble();
            if (vote == 0)
                continue;

            approved |= vote > 0;
            changesRequested |= vote < 0;
            entries.Add(new RemotePrConversationEntry(
                "review",
                ReadString(reviewer, "displayName") ?? string.Empty,
                string.Empty,
                DateTime.MinValue,
                vote > 0 ? "APPROVED" : "CHANGES_REQUESTED"));
        }

        return (approved, changesRequested, entries);
    }

    private async Task<IReadOnlyList<RemotePrConversationEntry>> ReadConversationAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber, List<RemotePrConversationEntry> reviews)
    {
        var conversation = new List<RemotePrConversationEntry>(reviews);
        foreach (var comment in await ReadThreadCommentsAsync(http, repo, prNumber))
            conversation.Add(new RemotePrConversationEntry(comment.Kind, comment.Author, comment.Body, comment.At, null));

        return conversation.OrderBy(e => e.CreatedAt).ToList();
    }

    private sealed record ThreadComment(string Kind, string Id, string Author, string Body, DateTime At);

    /// <summary>
    /// Every human comment on a PR, flattened out of the thread structure Azure
    /// DevOps keeps them in — the one place that knows that wire shape, since
    /// both the comment list and the snapshot's conversation are drawn from it.
    /// Comments Azure DevOps writes itself ("updated the source branch", "set
    /// auto-complete") are events rather than anything a person said, and are
    /// dropped; a thread anchored to a file is an inline diff comment, one that
    /// is not is the PR-level discussion. Comment ids restart per thread, so the
    /// id carried out is qualified by the thread's.
    /// </summary>
    private async Task<IReadOnlyList<ThreadComment>> ReadThreadCommentsAsync(
        HttpClient http, ResolvedRemoteRepository repo, string prNumber)
    {
        var flattened = new List<ThreadComment>();

        foreach (var thread in await GetValuesAsync(http, Versioned($"{PrApi(repo, prNumber)}/threads")))
        {
            var threadId = ReadScalar(thread, "id") ?? "0";
            var kind = ReadString(Child(thread, "threadContext"), "filePath") is null ? "comment" : "review_comment";
            if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var comment in comments.EnumerateArray())
            {
                var content = ReadString(comment, "content");
                if (string.IsNullOrWhiteSpace(content)
                    || string.Equals(ReadString(comment, "commentType"), "system", StringComparison.OrdinalIgnoreCase)
                    || (comment.TryGetProperty("isDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True))
                    continue;

                flattened.Add(new ThreadComment(
                    kind,
                    $"{threadId}-{ReadScalar(comment, "id") ?? "0"}",
                    ReadString(Child(comment, "author"), "displayName") ?? string.Empty,
                    content,
                    ReadDate(comment, "publishedDate") ?? DateTime.MinValue));
            }
        }

        return flattened;
    }

    /// <summary>
    /// The PR's CI verdict, from both places Azure DevOps reports one: the
    /// branch-policy evaluations (where an Azure Pipelines build lands, carrying
    /// the build id <c>get_ci_log</c> needs) and the PR statuses an external CI
    /// posts. Non-build policies — reviewer counts, linked work items — are not
    /// CI and are left out of the verdict.
    /// </summary>
    private async Task<(RemotePrCiStatus Status, IReadOnlyList<RemotePrCheck> FailedChecks)> ReadCiStatusAsync(
        HttpClient http, ResolvedRemoteRepository repo, JsonElement pr, string prNumber)
    {
        var anyPresent = false;
        var anyPending = false;
        var failed = new List<RemotePrCheck>();

        var projectId = ReadString(Child(Child(pr, "repository"), "project"), "id");
        if (projectId is not null)
        {
            var artifactId = $"vstfs:///CodeReview/CodeReviewId/{projectId}/{prNumber}";
            var evaluations = await GetValuesAsync(http, Versioned(
                $"{repo.ApiBase}/_apis/policy/evaluations", $"artifactId={Uri.EscapeDataString(artifactId)}"));

            foreach (var evaluation in evaluations)
            {
                var configuration = Child(evaluation, "configuration");
                if (!string.Equals(ReadString(Child(configuration, "type"), "id"), BuildPolicyTypeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var status = (ReadString(evaluation, "status") ?? string.Empty).ToLowerInvariant();
                if (status is "notapplicable")
                    continue;

                anyPresent = true;
                if (status is "queued" or "running")
                    anyPending = true;
                else if (status is "rejected" or "broken")
                {
                    var buildId = ReadScalar(Child(evaluation, "context"), "buildId");
                    failed.Add(new RemotePrCheck(
                        ReadString(Child(configuration, "settings"), "displayName") ?? "Build",
                        status,
                        buildId is null ? null : $"{repo.ApiBase}/_build/results?buildId={buildId}",
                        null,
                        buildId));
                }
            }
        }

        foreach (var status in await GetValuesAsync(http, Versioned($"{PrApi(repo, prNumber)}/statuses")))
        {
            var state = (ReadString(status, "state") ?? string.Empty).ToLowerInvariant();
            if (state is "" or "notset" or "notapplicable")
                continue;

            anyPresent = true;
            if (state == "pending")
                anyPending = true;
            else if (state is "failed" or "error")
                failed.Add(new RemotePrCheck(
                    ReadString(Child(status, "context"), "name") ?? "status",
                    state,
                    ReadString(status, "targetUrl"),
                    Truncate(ReadString(status, "description")),
                    null));
        }

        if (failed.Count > 0) return (RemotePrCiStatus.Failed, failed);
        if (!anyPresent) return (RemotePrCiStatus.None, Array.Empty<RemotePrCheck>());
        return (anyPending ? RemotePrCiStatus.Pending : RemotePrCiStatus.Passed, Array.Empty<RemotePrCheck>());
    }

    /// <summary>Longest check description kept on a snapshot, matching the base class's own budget.</summary>
    private const int MaxCheckSummaryLength = 1000;

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= MaxCheckSummaryLength ? trimmed : trimmed[..MaxCheckSummaryLength] + "…";
    }

    private async Task<string?> ResolveFailedLogIdAsync(HttpClient http, string buildApi)
    {
        var records = await GetValuesAsync(http, Versioned($"{buildApi}/timeline"), "records");
        var failedLog = records
            .Where(r => (ReadString(r, "result") ?? string.Empty).ToLowerInvariant() is "failed" or "canceled")
            .Select(r => ReadScalar(Child(r, "log"), "id"))
            .LastOrDefault(id => id is not null);
        if (failedLog is not null)
            return failedLog;

        return (await GetValuesAsync(http, Versioned($"{buildApi}/logs")))
            .Select(l => ReadScalar(l, "id"))
            .LastOrDefault(id => id is not null);
    }

    /// <summary>
    /// GET a collection response, whose payload Azure DevOps wraps in
    /// <c>value</c> (the build timeline being the exception, wrapping in
    /// <c>records</c>); empty on any failure.
    /// </summary>
    private static async Task<IReadOnlyList<JsonElement>> GetValuesAsync(HttpClient http, string url, string property = "value")
    {
        var root = await GetObjectAsync(http, url);
        if (root is null || !root.Value.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<JsonElement>();

        return values.EnumerateArray().Select(e => e.Clone()).ToList();
    }

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

    private static JsonElement? Child(JsonElement? element, string property)
        => element is { ValueKind: JsonValueKind.Object } && element.Value.TryGetProperty(property, out var value)
            ? value
            : null;

    private static string? ReadString(JsonElement? element, string property)
    {
        var value = Child(element, property);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    /// <summary>An id-like field as a string, whether the service sends it as a number or a string.</summary>
    private static string? ReadScalar(JsonElement? element, string property)
    {
        var value = Child(element, property);
        return value?.ValueKind switch
        {
            JsonValueKind.Number => value.Value.GetRawText(),
            JsonValueKind.String => value.Value.GetString(),
            _ => null,
        };
    }

    private static DateTime? ReadDate(JsonElement? element, string property)
    {
        var value = Child(element, property);
        return value?.ValueKind == JsonValueKind.String && value.Value.TryGetDateTime(out var parsed) ? parsed : null;
    }
}
