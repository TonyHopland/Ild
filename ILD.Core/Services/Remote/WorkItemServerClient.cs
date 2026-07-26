using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Typed HTTP client for the standalone WorkItem server. Stateless — the
/// caller supplies <see cref="WorkItemServerOptions"/> per call so a single
/// ILD instance can talk to multiple remote providers without rebuilding the
/// HttpClient.
/// </summary>
public interface IWorkItemServerClient
{
    Task<RemoteWorkItem> CreateAsync(WorkItemServerOptions opts, RemoteCreateWorkItemRequest req, CancellationToken ct = default);
    Task<RemoteWorkItem?> GetAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteWorkItem>> ListAsync(WorkItemServerOptions opts, RemoteWorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default);
    Task<RemoteWorkItem?> UpdateAsync(WorkItemServerOptions opts, string id, RemoteUpdateWorkItemRequest req, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default);

    Task<RemoteTransitionResponse> TransitionAsync(WorkItemServerOptions opts, string id, RemoteTransitionRequest req, CancellationToken ct = default);
    Task<bool> AddDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default);
    Task<bool> RemoveDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default);
    Task<bool> AppendFeedbackAsync(WorkItemServerOptions opts, string id, string content, CancellationToken ct = default);
    Task<bool> AppendConversationAsync(WorkItemServerOptions opts, string id, string role, string content, string? name, CancellationToken ct = default);

    /// <summary>
    /// Record a PR against a work item on the server, keyed by URL — reporting
    /// the same PR again updates it in place instead of duplicating it, so
    /// callers can report freely (on creation, on merge, or while reconciling).
    /// <paramref name="createdAt"/> is the start of the run that opened it, so
    /// the server keeps the item's PRs in the runs' order.
    ///
    /// False means only that this attempt did not record it — a server that is
    /// unreachable, too old to know the endpoint, missing the work item, or
    /// busy with a competing writer (409). None of them is worth special-casing
    /// here: the caller reports the PR again on its next pass.
    /// </summary>
    Task<bool> RecordPullRequestAsync(WorkItemServerOptions opts, string id, string url, Guid? loopRunId, bool merged, DateTime? createdAt, CancellationToken ct = default);

    Task<RemotePollResponse> PollAsync(WorkItemServerOptions opts, IReadOnlyList<string> activeIds, CancellationToken ct = default);
}

public sealed class WorkItemServerClient : IWorkItemServerClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WorkItemServerClient(HttpClient http) => _http = http;

    private HttpRequestMessage Build(WorkItemServerOptions opts, HttpMethod method, string relative)
    {
        var baseUrl = opts.BaseUrl.TrimEnd('/');
        var msg = new HttpRequestMessage(method, $"{baseUrl}{relative}");
        if (!string.IsNullOrEmpty(opts.ApiKey))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        return msg;
    }

    public async Task<RemoteWorkItem> CreateAsync(WorkItemServerOptions opts, RemoteCreateWorkItemRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, "/workitems");
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RemoteWorkItem>(JsonOpts, ct))!;
    }

    public async Task<RemoteWorkItem?> GetAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Get, $"/workitems/{id}");
        var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RemoteWorkItem>(JsonOpts, ct);
    }

    public async Task<IReadOnlyList<RemoteWorkItem>> ListAsync(WorkItemServerOptions opts, RemoteWorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (status.HasValue) qs.Add($"status={(int)status.Value}");
        if (tags is { Count: > 0 }) qs.Add($"tags={Uri.EscapeDataString(string.Join(',', tags))}");
        var query = qs.Count == 0 ? string.Empty : "?" + string.Join('&', qs);

        var msg = Build(opts, HttpMethod.Get, $"/workitems{query}");
        var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<RemoteWorkItem>>(JsonOpts, ct))!;
    }

    public async Task<RemoteWorkItem?> UpdateAsync(WorkItemServerOptions opts, string id, RemoteUpdateWorkItemRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Put, $"/workitems/{id}");
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RemoteWorkItem>(JsonOpts, ct);
    }

    public async Task<bool> DeleteAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Delete, $"/workitems/{id}");
        var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<RemoteTransitionResponse> TransitionAsync(WorkItemServerOptions opts, string id, RemoteTransitionRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/transition");
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new RemoteTransitionResponse { Success = false, ActualStatus = RemoteWorkItemStatus.Backlog, Reason = "Not found" };
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RemoteTransitionResponse>(JsonOpts, ct))!;
    }

    public async Task<bool> AddDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/dependencies");
        msg.Content = JsonContent.Create(new { dependencyId }, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Delete, $"/workitems/{id}/dependencies/{dependencyId}");
        var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AppendFeedbackAsync(WorkItemServerOptions opts, string id, string content, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/feedback");
        msg.Content = JsonContent.Create(new { content }, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AppendConversationAsync(WorkItemServerOptions opts, string id, string role, string content, string? name, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/conversation");
        msg.Content = JsonContent.Create(new { role, content, name }, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RecordPullRequestAsync(WorkItemServerOptions opts, string id, string url, Guid? loopRunId, bool merged, DateTime? createdAt, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/pull-requests");
        msg.Content = JsonContent.Create(new { url, loopRunId, merged, createdAt }, options: JsonOpts);
        var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<RemotePollResponse> PollAsync(WorkItemServerOptions opts, IReadOnlyList<string> activeIds, CancellationToken ct = default)
    {
        var query = activeIds.Count == 0
            ? string.Empty
            : "?activeIds=" + Uri.EscapeDataString(string.Join(',', activeIds));
        var msg = Build(opts, HttpMethod.Get, $"/workitems/poll{query}");
        var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RemotePollResponse>(JsonOpts, ct))!;
    }
}
