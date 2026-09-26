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
    Task<bool> AppendConversationAsync(WorkItemServerOptions opts, string id, string role, string content, string? name, Guid? runNodeId = null, CancellationToken ct = default);

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

    /// <summary>The work item's attachments, metadata only. Null when there is no such work item.</summary>
    Task<IReadOnlyList<RemoteWorkItemAttachment>?> ListAttachmentsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Store files against a work item. The server enforces the size, count and
    /// per-item total limits, so a refusal comes back as an outcome carrying its
    /// message rather than as the exception every other call here throws — see
    /// <see cref="AttachmentUploadResult"/>.
    /// </summary>
    Task<AttachmentUploadResult> UploadAttachmentsAsync(WorkItemServerOptions opts, string workItemId, IReadOnlyList<RemoteAttachmentUpload> files, CancellationToken ct = default);

    /// <summary>The bytes of one attachment, with the content type to serve it as. Null when either id is unknown.</summary>
    Task<(byte[] Content, string ContentType, string FileName)?> GetAttachmentAsync(WorkItemServerOptions opts, string workItemId, Guid attachmentId, CancellationToken ct = default);

    Task<bool> DeleteAttachmentAsync(WorkItemServerOptions opts, string workItemId, Guid attachmentId, CancellationToken ct = default);

    /// <summary>
    /// Propose an edit to a work item. The server validates it and caps the
    /// pending proposals per item, and a refusal comes back as an outcome
    /// carrying its reason — see <see cref="EditProposalCreateResult"/>.
    /// </summary>
    Task<EditProposalCreateResult> CreateEditProposalAsync(WorkItemServerOptions opts, string workItemId, RemoteCreateEditProposalRequest req, CancellationToken ct = default);

    /// <summary>The work item's proposals, newest first. Null when there is no such work item.</summary>
    Task<IReadOnlyList<RemoteWorkItemEditProposal>?> ListEditProposalsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct = default);

    /// <summary>Proposals across work items, newest first.</summary>
    Task<IReadOnlyList<RemoteWorkItemEditProposal>> QueryEditProposalsAsync(WorkItemServerOptions opts, RemoteEditProposalQuery query, CancellationToken ct = default);

    /// <summary>
    /// Apply a pending proposal if the item's editable fields still equal its
    /// snapshot, atomically on the server; otherwise it goes Stale and nothing
    /// is applied. A refusal is an outcome, not an exception.
    /// </summary>
    Task<EditProposalDecisionResult> ApproveEditProposalAsync(WorkItemServerOptions opts, string workItemId, Guid proposalId, CancellationToken ct = default);

    Task<EditProposalDecisionResult> RejectEditProposalAsync(WorkItemServerOptions opts, string workItemId, Guid proposalId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Record that the proposing chat has been told these decisions. Ids of
    /// proposals still pending are ignored.
    /// </summary>
    Task MarkEditProposalDecisionsDeliveredAsync(WorkItemServerOptions opts, IReadOnlyList<Guid> proposalIds, CancellationToken ct = default);
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

    /// <summary>
    /// Same contract as <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>
    /// — an <see cref="HttpRequestException"/>, which every caller already treats
    /// as "server unreachable" — but naming the server that answered. Which
    /// WorkItem Server a refusal came from is the whole diagnosis when more than
    /// one is running (a worktree preview of ILD runs its own alongside the host's),
    /// and the framework message says only "401 (Unauthorized)".
    /// </summary>
    private static void EnsureSuccess(HttpResponseMessage resp, HttpRequestMessage msg)
    {
        if (resp.IsSuccessStatusCode)
            return;

        var detail = resp.StatusCode == HttpStatusCode.Unauthorized
            ? " The configured API key is not one of that server's WORKITEM_API_KEYS."
            : string.Empty;

        throw new HttpRequestException(
            $"{msg.Method} {msg.RequestUri} was refused: {(int)resp.StatusCode} {resp.ReasonPhrase}.{detail}",
            inner: null,
            statusCode: resp.StatusCode);
    }

    public async Task<RemoteWorkItem> CreateAsync(WorkItemServerOptions opts, RemoteCreateWorkItemRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, "/workitems");
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<RemoteWorkItem>(JsonOpts, ct))!;
    }

    public async Task<RemoteWorkItem?> GetAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Get, $"/workitems/{id}");
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(resp, msg);
        return await resp.Content.ReadFromJsonAsync<RemoteWorkItem>(JsonOpts, ct);
    }

    public async Task<IReadOnlyList<RemoteWorkItem>> ListAsync(WorkItemServerOptions opts, RemoteWorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (status.HasValue) qs.Add($"status={(int)status.Value}");
        if (tags is { Count: > 0 }) qs.Add($"tags={Uri.EscapeDataString(string.Join(',', tags))}");
        var query = qs.Count == 0 ? string.Empty : "?" + string.Join('&', qs);

        var msg = Build(opts, HttpMethod.Get, $"/workitems{query}");
        using var resp = await _http.SendAsync(msg, ct);
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<List<RemoteWorkItem>>(JsonOpts, ct))!;
    }

    public async Task<RemoteWorkItem?> UpdateAsync(WorkItemServerOptions opts, string id, RemoteUpdateWorkItemRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Put, $"/workitems/{id}");
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(resp, msg);
        return await resp.Content.ReadFromJsonAsync<RemoteWorkItem>(JsonOpts, ct);
    }

    public async Task<bool> DeleteAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Delete, $"/workitems/{id}");
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<RemoteTransitionResponse> TransitionAsync(WorkItemServerOptions opts, string id, RemoteTransitionRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/transition");
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new RemoteTransitionResponse { Success = false, ActualStatus = RemoteWorkItemStatus.Backlog, Reason = "Not found" };
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<RemoteTransitionResponse>(JsonOpts, ct))!;
    }

    public async Task<bool> AddDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/dependencies");
        msg.Content = JsonContent.Create(new { dependencyId }, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Delete, $"/workitems/{id}/dependencies/{dependencyId}");
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AppendFeedbackAsync(WorkItemServerOptions opts, string id, string content, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/feedback");
        msg.Content = JsonContent.Create(new { content }, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AppendConversationAsync(WorkItemServerOptions opts, string id, string role, string content, string? name, Guid? runNodeId = null, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/conversation");
        msg.Content = JsonContent.Create(new { role, content, name, runNodeId }, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RecordPullRequestAsync(WorkItemServerOptions opts, string id, string url, Guid? loopRunId, bool merged, DateTime? createdAt, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, $"/workitems/{id}/pull-requests");
        msg.Content = JsonContent.Create(new { url, loopRunId, merged, createdAt }, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<RemotePollResponse> PollAsync(WorkItemServerOptions opts, IReadOnlyList<string> activeIds, CancellationToken ct = default)
    {
        var query = activeIds.Count == 0
            ? string.Empty
            : "?activeIds=" + Uri.EscapeDataString(string.Join(',', activeIds));
        var msg = Build(opts, HttpMethod.Get, $"/workitems/poll{query}");
        using var resp = await _http.SendAsync(msg, ct);
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<RemotePollResponse>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<RemoteWorkItemAttachment>?> ListAttachmentsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Get, AttachmentsPath(workItemId));
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<List<RemoteWorkItemAttachment>>(JsonOpts, ct))!;
    }

    public async Task<AttachmentUploadResult> UploadAttachmentsAsync(WorkItemServerOptions opts, string workItemId, IReadOnlyList<RemoteAttachmentUpload> files, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, AttachmentsPath(workItemId));
        using var body = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Content);
            // Without validation: the server is the one that decides what a
            // usable content type is, and it answers for an unusable one by
            // storing application/octet-stream.
            if (!string.IsNullOrWhiteSpace(file.ContentType))
                part.Headers.TryAddWithoutValidation("Content-Type", file.ContentType);
            body.Add(part, AttachmentFieldName, HeaderSafeFileName(file.FileName));
        }
        msg.Content = body;

        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new AttachmentUploadResult(AttachmentUploadOutcome.NotFound, "No such work item.", Array.Empty<RemoteWorkItemAttachment>());
        if (resp.StatusCode == HttpStatusCode.BadRequest)
            return new AttachmentUploadResult(
                AttachmentUploadOutcome.Rejected, await ReadErrorAsync(resp, "The upload was refused.", ct), Array.Empty<RemoteWorkItemAttachment>());

        EnsureSuccess(resp, msg);
        return new AttachmentUploadResult(
            AttachmentUploadOutcome.Created,
            null,
            (await resp.Content.ReadFromJsonAsync<List<RemoteWorkItemAttachment>>(JsonOpts, ct))!);
    }

    public async Task<(byte[] Content, string ContentType, string FileName)?> GetAttachmentAsync(WorkItemServerOptions opts, string workItemId, Guid attachmentId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Get, $"{AttachmentsPath(workItemId)}/{attachmentId}");
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(resp, msg);

        var content = await resp.Content.ReadAsByteArrayAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? attachmentId.ToString();
        return (content, contentType, fileName);
    }

    public async Task<bool> DeleteAttachmentAsync(WorkItemServerOptions opts, string workItemId, Guid attachmentId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Delete, $"{AttachmentsPath(workItemId)}/{attachmentId}");
        using var resp = await _http.SendAsync(msg, ct);
        // False means the attachment is not there. Anything else the server says
        // is a failure of the server, and reporting it as "already gone" would
        // tell a user their file was deleted when it is still sitting in a
        // database that happens to be unreachable.
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        EnsureSuccess(resp, msg);
        return true;
    }

    public async Task<EditProposalCreateResult> CreateEditProposalAsync(WorkItemServerOptions opts, string workItemId, RemoteCreateEditProposalRequest req, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, EditProposalsPath(workItemId));
        msg.Content = JsonContent.Create(req, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        switch (resp.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return new EditProposalCreateResult(EditProposalCreateOutcome.NotFound, "No such work item.", null);
            case HttpStatusCode.BadRequest:
                return new EditProposalCreateResult(
                    EditProposalCreateOutcome.Invalid, await ReadErrorAsync(resp, "The proposal was refused.", ct), null);
            case HttpStatusCode.Conflict:
                return new EditProposalCreateResult(
                    EditProposalCreateOutcome.TooManyPending, await ReadErrorAsync(resp, "The work item has too many pending proposals.", ct), null);
        }
        EnsureSuccess(resp, msg);
        return new EditProposalCreateResult(
            EditProposalCreateOutcome.Created,
            null,
            await resp.Content.ReadFromJsonAsync<RemoteWorkItemEditProposal>(JsonOpts, ct));
    }

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>?> ListEditProposalsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Get, EditProposalsPath(workItemId));
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<List<RemoteWorkItemEditProposal>>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>> QueryEditProposalsAsync(WorkItemServerOptions opts, RemoteEditProposalQuery query, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (query.Status is { } status) qs.Add($"status={(int)status}");
        if (query.CreatedByChatSessionId is { } chat) qs.Add($"createdByChatSessionId={chat}");
        if (query.UndeliveredOnly) qs.Add("undelivered=true");
        var suffix = qs.Count == 0 ? string.Empty : "?" + string.Join('&', qs);

        var msg = Build(opts, HttpMethod.Get, $"/edit-proposals{suffix}");
        using var resp = await _http.SendAsync(msg, ct);
        EnsureSuccess(resp, msg);
        return (await resp.Content.ReadFromJsonAsync<List<RemoteWorkItemEditProposal>>(JsonOpts, ct))!;
    }

    public Task<EditProposalDecisionResult> ApproveEditProposalAsync(WorkItemServerOptions opts, string workItemId, Guid proposalId, CancellationToken ct = default)
        => DecideEditProposalAsync(opts, workItemId, proposalId, "approve", body: null, ct);

    public Task<EditProposalDecisionResult> RejectEditProposalAsync(WorkItemServerOptions opts, string workItemId, Guid proposalId, string? reason, CancellationToken ct = default)
        => DecideEditProposalAsync(opts, workItemId, proposalId, "reject", new { reason }, ct);

    /// <summary>
    /// The server answers a refused decision with a 409 whose body says why and
    /// carries the proposal as it stands, so the refusal travels back as an
    /// outcome rather than as the exception an outage is.
    /// </summary>
    private async Task<EditProposalDecisionResult> DecideEditProposalAsync(
        WorkItemServerOptions opts, string workItemId, Guid proposalId, string action, object? body, CancellationToken ct)
    {
        var msg = Build(opts, HttpMethod.Post, $"{EditProposalsPath(workItemId)}/{proposalId}/{action}");
        msg.Content = JsonContent.Create(body ?? new { }, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new EditProposalDecisionResult(EditProposalDecisionOutcome.NotFound, null, null);
        if (resp.StatusCode != HttpStatusCode.Conflict)
            EnsureSuccess(resp, msg);

        var decision = (await resp.Content.ReadFromJsonAsync<EditProposalDecisionBody>(JsonOpts, ct))!;
        return new EditProposalDecisionResult(decision.Outcome, decision.Proposal, decision.WorkItem);
    }

    private sealed class EditProposalDecisionBody
    {
        public EditProposalDecisionOutcome Outcome { get; set; }
        public RemoteWorkItemEditProposal? Proposal { get; set; }
        public RemoteWorkItem? WorkItem { get; set; }
    }

    public async Task MarkEditProposalDecisionsDeliveredAsync(WorkItemServerOptions opts, IReadOnlyList<Guid> proposalIds, CancellationToken ct = default)
    {
        var msg = Build(opts, HttpMethod.Post, "/edit-proposals/delivered");
        msg.Content = JsonContent.Create(new { ids = proposalIds }, options: JsonOpts);
        using var resp = await _http.SendAsync(msg, ct);
        EnsureSuccess(resp, msg);
    }

    private static string EditProposalsPath(string workItemId)
        => $"/workitems/{Uri.EscapeDataString(workItemId)}/edit-proposals";

    /// <summary>The form field the server's attachment endpoint reads files from.</summary>
    private const string AttachmentFieldName = "files";

    /// <summary>
    /// A name <see cref="MultipartFormDataContent"/> will put in a header. It
    /// refuses a blank one and one holding a quote or a newline, and the name is
    /// the user's — <c>my "photo".png</c> is an ordinary file, not a bad request —
    /// so those characters are replaced rather than the upload refused.
    /// </summary>
    private static string HeaderSafeFileName(string? fileName)
    {
        var trimmed = (fileName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "attachment";

        var safe = new string(trimmed.Select(c => c == '"' || char.IsControl(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "attachment" : safe;
    }

    private static string AttachmentsPath(string workItemId)
        => $"/workitems/{Uri.EscapeDataString(workItemId)}/attachments";

    /// <summary>
    /// The <c>{ error }</c> a refusal carries, so the reason a file or a proposal
    /// was turned away survives the trip back to whoever sent it. Falls back to
    /// the raw body, which is all an older server might send.
    /// </summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, string fallback, CancellationToken ct)
    {
        var raw = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException) { /* not JSON — the raw body is the best answer there is */ }
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw;
    }
}
