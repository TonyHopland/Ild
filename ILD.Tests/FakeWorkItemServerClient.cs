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

    public FakeWorkItemServerClient(IWorkItemService svc) => _svc = svc;

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
            .Select(m => new RemoteConversationMessage(m.Role, m.Content, m.Timestamp))
            .ToList(),
        HumanFeedbackActions = dto.HumanFeedbackActions,
        CreatedByLoopRunId = dto.CreatedByLoopRunId,
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
            Dependencies = req.Dependencies?.ToList() ?? new List<Guid>(),
            ForceStatus = req.ForceStatus.HasValue ? Map(req.ForceStatus.Value) : null,
            CreatedByLoopRunId = req.CreatedByLoopRunId,
        }, ct);
        return ToRemote(dto);
    }

    public async Task<RemoteWorkItem?> GetAsync(WorkItemServerOptions opts, Guid id, CancellationToken ct = default)
    {
        var dto = await _svc.GetAsync(id, ct);
        return dto == null ? null : ToRemote(dto);
    }

    public async Task<IReadOnlyList<RemoteWorkItem>> ListAsync(WorkItemServerOptions opts, RemoteWorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var list = await _svc.ListAsync(status.HasValue ? Map(status.Value) : null, tags, ct);
        return list.Select(ToRemote).ToList();
    }

    public async Task<RemoteWorkItem?> UpdateAsync(WorkItemServerOptions opts, Guid id, RemoteUpdateWorkItemRequest req, CancellationToken ct = default)
    {
        var dto = await _svc.UpdateAsync(id, new UpdateWorkItemRequest
        {
            Title = req.Title,
            Description = req.Description,
            Tags = req.Tags?.ToList(),
        }, ct);
        return dto == null ? null : ToRemote(dto);
    }

    public Task<bool> DeleteAsync(WorkItemServerOptions opts, Guid id, CancellationToken ct = default) => _svc.DeleteAsync(id, ct);

    public async Task<RemoteTransitionResponse> TransitionAsync(WorkItemServerOptions opts, Guid id, RemoteTransitionRequest req, CancellationToken ct = default)
    {
        var resp = await _svc.TransitionAsync(id, new TransitionRequest
        {
            TargetStatus = Map(req.TargetStatus),
            Reason = req.Reason,
            Actions = req.Actions,
        }, ct);
        return new RemoteTransitionResponse
        {
            Success = resp.Success,
            ActualStatus = MapBack(resp.ActualStatus),
            Reason = resp.Reason,
        };
    }

    public Task<bool> AddDependencyAsync(WorkItemServerOptions opts, Guid id, Guid dependencyId, CancellationToken ct = default)
        => _svc.AddDependencyAsync(id, dependencyId, ct);

    public Task<bool> RemoveDependencyAsync(WorkItemServerOptions opts, Guid id, Guid dependencyId, CancellationToken ct = default)
        => _svc.RemoveDependencyAsync(id, dependencyId, ct);

    public Task<bool> AppendFeedbackAsync(WorkItemServerOptions opts, Guid id, string content, CancellationToken ct = default)
        => _svc.AppendFeedbackAsync(id, content, ct);

    public async Task<RemotePollResponse> PollAsync(WorkItemServerOptions opts, IReadOnlyList<Guid> activeIds, CancellationToken ct = default)
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

    public Task<WorkItemServerOptions> ResolveForWorkItemAsync(Guid workItemId, CancellationToken ct = default)
        => Task.FromResult(new WorkItemServerOptions { BaseUrl = "http://localhost", ApiKey = "test-key" });
}
