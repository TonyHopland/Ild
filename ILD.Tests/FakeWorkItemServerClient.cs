using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;

namespace ILD.Tests;

/// <summary>
/// In-memory fake implementing <see cref="IWorkItemServerClient"/> by
/// delegating to a real <see cref="IWorkItemService"/> instance. Tests get
/// the full server semantics (atomic claims, dep validation, conversation
/// appends, stale reclaim) without standing up an HTTP listener.
/// </summary>
public sealed class FakeWorkItemServerClient : IWorkItemServerClient
{
    private readonly IWorkItemService _svc;
    private readonly IWorkItemAttachmentService _attachments;
    private readonly IWorkItemEditProposalService _proposals;

    public FakeWorkItemServerClient(IWorkItemService svc, IWorkItemAttachmentService attachments, IWorkItemEditProposalService proposals)
    {
        _svc = svc;
        _attachments = attachments;
        _proposals = proposals;
    }

    private static ILD.WorkItemServer.Domain.WorkItemStatus Map(RemoteWorkItemStatus s) => (ILD.WorkItemServer.Domain.WorkItemStatus)(int)s;
    private static RemoteWorkItemStatus MapBack(ILD.WorkItemServer.Domain.WorkItemStatus s) => (RemoteWorkItemStatus)(int)s;
    private static ILD.WorkItemServer.Domain.WorkItemPriority MapPri(RemoteWorkItemPriority p) => (ILD.WorkItemServer.Domain.WorkItemPriority)(int)p;
    private static RemoteWorkItemPriority MapPriBack(ILD.WorkItemServer.Domain.WorkItemPriority p) => (RemoteWorkItemPriority)(int)p;

    private static RemoteWorkItem ToRemote(WorkItemDto dto) => new()
    {
        Id = dto.Id,
        Title = dto.Title,
        Description = dto.Description,
        CreatedBy = dto.CreatedBy,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt,
        Priority = MapPriBack(dto.Priority),
        Status = MapBack(dto.Status),
        Tags = dto.Tags,
        Dependencies = dto.Dependencies,
        Conversation = dto.Conversation
            .Select(m => new RemoteConversationMessage(m.Role, m.Content, m.Timestamp, m.Name, m.RunNodeId))
            .ToList(),
        PullRequests = dto.PullRequests
            .Select(p => new RemoteWorkItemPullRequest(p.Url, p.LoopRunId, p.Merged, p.CreatedAt))
            .ToList(),
        Attachments = dto.Attachments
            .Select(a => new RemoteWorkItemAttachment(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.CreatedAt))
            .ToList(),
        HumanFeedbackActions = dto.HumanFeedbackActions,
        CreatedByLoopRunId = dto.CreatedByLoopRunId,
        CreatedByChatSessionId = dto.CreatedByChatSessionId,
        RepositoryId = dto.RepositoryId,
        BranchNameOverride = dto.BranchNameOverride,
        BaseBranchOverride = dto.BaseBranchOverride,
        PendingEditProposalCount = dto.PendingEditProposalCount,
    };

    public async Task<RemoteWorkItem> CreateAsync(WorkItemServerOptions opts, RemoteCreateWorkItemRequest req, CancellationToken ct = default)
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = req.Title,
            Description = req.Description,
            CreatedBy = req.CreatedBy,
            Priority = MapPri(req.Priority),
            Tags = req.Tags?.ToList() ?? new List<string>(),
            Dependencies = req.Dependencies?.ToList() ?? new List<string>(),
            ForceStatus = req.ForceStatus.HasValue ? Map(req.ForceStatus.Value) : null,
            CreatedByLoopRunId = req.CreatedByLoopRunId,
            CreatedByChatSessionId = req.CreatedByChatSessionId,
            RepositoryId = req.RepositoryId,
            BranchNameOverride = req.BranchNameOverride,
            BaseBranchOverride = req.BaseBranchOverride,
        }, ct);
        return ToRemote(dto);
    }

    public async Task<RemoteWorkItem?> GetAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default)
    {
        var dto = await _svc.GetAsync(id, ct);
        return dto == null ? null : ToRemote(dto);
    }

    public async Task<IReadOnlyList<RemoteWorkItem>> ListAsync(WorkItemServerOptions opts, RemoteWorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var list = await _svc.ListAsync(status.HasValue ? Map(status.Value) : null, tags, ct);
        return list.Select(ToRemote).ToList();
    }

    public async Task<RemoteWorkItem?> UpdateAsync(WorkItemServerOptions opts, string id, RemoteUpdateWorkItemRequest req, CancellationToken ct = default)
    {
        var dto = await _svc.UpdateAsync(id, new UpdateWorkItemRequest
        {
            Title = req.Title,
            Description = req.Description,
            Tags = req.Tags?.ToList(),
            BranchNameOverride = req.BranchNameOverride,
            BaseBranchOverride = req.BaseBranchOverride,
        }, ct);
        return dto == null ? null : ToRemote(dto);
    }

    public Task<bool> DeleteAsync(WorkItemServerOptions opts, string id, CancellationToken ct = default) => _svc.DeleteAsync(id, ct);

    public async Task<RemoteTransitionResponse> TransitionAsync(WorkItemServerOptions opts, string id, RemoteTransitionRequest req, CancellationToken ct = default)
    {
        var resp = await _svc.TransitionAsync(id, new TransitionRequest
        {
            TargetStatus = Map(req.TargetStatus),
            Reason = req.Reason,
            Actions = req.Actions,
            Name = req.Name,
            RunNodeId = req.RunNodeId,
        }, ct);
        return new RemoteTransitionResponse
        {
            Success = resp.Success,
            ActualStatus = MapBack(resp.ActualStatus),
            Reason = resp.Reason,
        };
    }

    public Task<bool> AddDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default)
        => _svc.AddDependencyAsync(id, dependencyId, ct);

    public Task<bool> RemoveDependencyAsync(WorkItemServerOptions opts, string id, string dependencyId, CancellationToken ct = default)
        => _svc.RemoveDependencyAsync(id, dependencyId, ct);

    public Task<bool> AppendFeedbackAsync(WorkItemServerOptions opts, string id, string content, CancellationToken ct = default)
        => _svc.AppendFeedbackAsync(id, content, ct);

    public Task<bool> AppendConversationAsync(WorkItemServerOptions opts, string id, string role, string content, string? name, Guid? runNodeId = null, CancellationToken ct = default)
        => _svc.AppendConversationAsync(id, role, content, name, runNodeId, ct);

    /// <summary>
    /// Mirrors the HTTP client, which reports success as the status code: only
    /// a recorded PR is a success, and every failure — 400/404/409 — is false.
    /// </summary>
    public async Task<bool> RecordPullRequestAsync(WorkItemServerOptions opts, string id, string url, Guid? loopRunId, bool merged, DateTime? createdAt, CancellationToken ct = default)
        => await _svc.RecordPullRequestAsync(id, new RecordPullRequestRequest
        {
            Url = url,
            LoopRunId = loopRunId,
            Merged = merged,
            CreatedAt = createdAt,
        }, ct) == RecordPullRequestOutcome.Recorded;

    public async Task<IReadOnlyList<RemoteWorkItemAttachment>?> ListAttachmentsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct = default)
    {
        var listed = await _attachments.ListAsync(workItemId, ct);
        return listed?.Select(ToRemote).ToList();
    }

    /// <summary>
    /// Mirrors the HTTP client's mapping of the server's answers: 404 for an
    /// unknown work item, and a 400 whose message says which limit was broken.
    /// </summary>
    public async Task<AttachmentUploadResult> UploadAttachmentsAsync(WorkItemServerOptions opts, string workItemId, IReadOnlyList<RemoteAttachmentUpload> files, CancellationToken ct = default)
    {
        var result = await _attachments.AddAsync(
            workItemId,
            files.Select(f => new IncomingAttachment(
                f.FileName, f.ContentType, f.Content.LongLength, _ => Task.FromResult(f.Content))).ToList(),
            ct);

        var outcome = result.Outcome switch
        {
            AddAttachmentsOutcome.Created => AttachmentUploadOutcome.Created,
            AddAttachmentsOutcome.NotFound => AttachmentUploadOutcome.NotFound,
            _ => AttachmentUploadOutcome.Rejected,
        };
        return new AttachmentUploadResult(outcome, result.Error, result.Created.Select(ToRemote).ToList());
    }

    public async Task<(byte[] Content, string ContentType, string FileName)?> GetAttachmentAsync(WorkItemServerOptions opts, string workItemId, Guid attachmentId, CancellationToken ct = default)
    {
        var stored = await _attachments.GetContentAsync(workItemId, attachmentId, ct);
        return stored == null ? null : (stored.Content, stored.ContentType, stored.FileName);
    }

    public Task<bool> DeleteAttachmentAsync(WorkItemServerOptions opts, string workItemId, Guid attachmentId, CancellationToken ct = default)
        => _attachments.DeleteAsync(workItemId, attachmentId, ct);

    private static RemoteWorkItemAttachment ToRemote(WorkItemAttachmentDto dto)
        => new(dto.Id, dto.FileName, dto.ContentType, dto.SizeBytes, dto.CreatedAt);


    /// <summary>
    /// Mirrors the HTTP client's mapping of the server's answers: 404 for an
    /// unknown item, 400 for an invalid proposal and 409 past the pending cap,
    /// each carrying the server's reason.
    /// </summary>
    public async Task<ILD.Core.Services.Remote.EditProposalCreateResult> CreateEditProposalAsync(WorkItemServerOptions opts, string workItemId, RemoteCreateEditProposalRequest req, CancellationToken ct = default)
    {
        var result = await _proposals.CreateAsync(workItemId, new CreateEditProposalRequest
        {
            Title = req.Title,
            Description = req.Description,
            Tags = req.Tags?.ToList(),
            BranchNameOverride = req.BranchNameOverride,
            BaseBranchOverride = req.BaseBranchOverride,
            Rationale = req.Rationale,
            CreatedByLoopRunId = req.CreatedByLoopRunId,
            CreatedByChatSessionId = req.CreatedByChatSessionId,
        }, ct);
        return new ILD.Core.Services.Remote.EditProposalCreateResult(
            (ILD.Core.Services.Remote.EditProposalCreateOutcome)(int)result.Outcome,
            result.Error,
            result.Proposal is null ? null : ToRemote(result.Proposal));
    }

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>?> ListEditProposalsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct = default)
        => (await _proposals.ListForItemAsync(workItemId, ct))?.Select(ToRemote).ToList();

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>> QueryEditProposalsAsync(WorkItemServerOptions opts, RemoteEditProposalQuery query, CancellationToken ct = default)
        => (await _proposals.ListAsync(
                query.Status is { } status ? (WorkItemEditProposalStatus)(int)status : null,
                query.CreatedByChatSessionId,
                query.UndeliveredOnly,
                ct))
            .Select(ToRemote).ToList();

    public async Task<ILD.Core.Services.Remote.EditProposalDecisionResult> ApproveEditProposalAsync(WorkItemServerOptions opts, string workItemId, Guid proposalId, CancellationToken ct = default)
        => ToRemote(await _proposals.ApproveAsync(workItemId, proposalId, ct));

    /// <summary>The server refuses an over-long reason with a 400, which the HTTP client throws on.</summary>
    public async Task<ILD.Core.Services.Remote.EditProposalDecisionResult> RejectEditProposalAsync(WorkItemServerOptions opts, string workItemId, Guid proposalId, string? reason, CancellationToken ct = default)
    {
        if (reason?.Trim().Length > WorkItemEditProposalService.MaxRejectionReasonLength)
            throw new HttpRequestException("The reject was refused: 400 Bad Request.", null, System.Net.HttpStatusCode.BadRequest);
        return ToRemote(await _proposals.RejectAsync(workItemId, proposalId, reason, ct));
    }

    public Task MarkEditProposalDecisionsDeliveredAsync(WorkItemServerOptions opts, IReadOnlyList<Guid> proposalIds, CancellationToken ct = default)
        => _proposals.MarkDecisionsDeliveredAsync(proposalIds, ct);

    private static ILD.Core.Services.Remote.EditProposalDecisionResult ToRemote(ILD.WorkItemServer.Services.EditProposalDecisionResult result)
        => new(
            (ILD.Core.Services.Remote.EditProposalDecisionOutcome)(int)result.Outcome,
            result.Proposal is null ? null : ToRemote(result.Proposal),
            result.WorkItem is null ? null : ToRemote(result.WorkItem));

    private static RemoteWorkItemEditProposal ToRemote(WorkItemEditProposalDto dto) => new()
    {
        Id = dto.Id,
        WorkItemId = dto.WorkItemId,
        Status = (RemoteEditProposalStatus)(int)dto.Status,
        Proposed = ToRemote(dto.Proposed),
        Snapshot = ToRemote(dto.Snapshot),
        Rationale = dto.Rationale,
        CreatedByLoopRunId = dto.CreatedByLoopRunId,
        CreatedByChatSessionId = dto.CreatedByChatSessionId,
        RejectionReason = dto.RejectionReason,
        CreatedAt = dto.CreatedAt,
        DecidedAt = dto.DecidedAt,
    };

    private static RemoteEditProposalFields ToRemote(EditProposalFieldsDto dto) => new()
    {
        Title = dto.Title,
        Description = dto.Description,
        Tags = dto.Tags,
        BranchNameOverride = dto.BranchNameOverride,
        BaseBranchOverride = dto.BaseBranchOverride,
    };

    public async Task<RemotePollResponse> PollAsync(WorkItemServerOptions opts, IReadOnlyList<string> activeIds, CancellationToken ct = default)
    {
        var resp = await _svc.PollAsync(activeIds, ct);
        return new RemotePollResponse
        {
            ActiveItems = resp.ActiveItems.Select(ToRemote).ToList(),
            ReadyItems = resp.ReadyItems.Select(ToRemote).ToList(),
        };
    }
}

/// <summary>
/// Stub options resolver returning a static value. Production code resolves
/// per-repository; tests don't care about routing.
/// </summary>
public sealed class StubWorkItemServerOptionsResolver : IWorkItemServerOptionsResolver
{
    public Task<WorkItemServerOptions> ResolveForRepositoryAsync(Guid? repositoryId, CancellationToken ct = default)
        => Task.FromResult(new WorkItemServerOptions { BaseUrl = "http://localhost", ApiKey = "test-key" });

    public Task<WorkItemServerOptions> ResolveForWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult(new WorkItemServerOptions { BaseUrl = "http://localhost", ApiKey = "test-key" });
}
