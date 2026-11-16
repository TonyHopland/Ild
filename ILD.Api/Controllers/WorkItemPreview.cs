using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>Why a work item's preview call did not produce a value.</summary>
public enum PreviewRefusal
{
    /// <summary>No such work item: 404, empty body.</summary>
    WorkItemNotFound,
    /// <summary>The work item has no active worktree: 400.</summary>
    NoWorktree,
    /// <summary>The call was malformed or the preview refused it (<see cref="InvalidOperationException"/>): 400.</summary>
    Refused,
    /// <summary>The service has no preview config: 404 with an error message.</summary>
    ConfigNotFound,
}

/// <summary>
/// The outcome of a <see cref="IWorkItemPreview"/> call: a value, or a typed refusal.
/// <see cref="ToActionResult"/> is the one mapping to HTTP both preview surfaces use.
/// </summary>
public sealed class PreviewResult<T>
{
    private readonly T? _value;

    private PreviewResult(T? value, PreviewRefusal? refusal, string? message)
    {
        _value = value;
        Refusal = refusal;
        Message = message;
    }

    public PreviewRefusal? Refusal { get; }

    public string? Message { get; }

    public T? Value => _value;

    public static PreviewResult<T> Ok(T value) => new(value, null, null);

    public static PreviewResult<T> Failed(PreviewRefusal refusal, string? message = null) => new(default, refusal, message);

    /// <summary>Transforms a value, passing a refusal through.</summary>
    public async Task<PreviewResult<TOut>> MapAsync<TOut>(Func<T, Task<TOut>> map)
        => Refusal is { } refusal ? PreviewResult<TOut>.Failed(refusal, Message) : PreviewResult<TOut>.Ok(await map(_value!));

    public IActionResult ToActionResult() => Refusal switch
    {
        null => new OkObjectResult(_value),
        PreviewRefusal.WorkItemNotFound => new NotFoundResult(),
        PreviewRefusal.ConfigNotFound => new NotFoundObjectResult(new { error = Message }),
        _ => new BadRequestObjectResult(new { error = Message }),
    };
}

/// <summary>
/// A work item's worktree preview, addressed by work item id. Each call resolves the work item and its worktree,
/// builds the start options, calls the preview, tells the board when the preview changed and turns the preview's
/// <see cref="InvalidOperationException"/> into <see cref="PreviewRefusal.Refused"/>. The UI's
/// <see cref="WorkItemsController"/> and the agent's <see cref="AgentController"/> (ADR-0011) are both route glue over it.
/// </summary>
public interface IWorkItemPreview
{
    Task<PreviewResult<WorktreePreviewResponse>> GetStatusAsync(string workItemId);

    Task<PreviewResult<WorktreePreviewResponse>> StartAsync(string workItemId, WorktreePreviewStartRequest? request);

    Task<PreviewResult<WorktreePreviewResponse>> StopAsync(string workItemId);

    /// <param name="startDependencies">
    /// Whether the service's dependencies start with it. The UI passes the request's choice; the agent route passes
    /// <c>false</c> because the agent has no such option today (no MCP tool exposes it).
    /// </param>
    Task<PreviewResult<WorktreePreviewResponse>> StartServiceAsync(
        string workItemId, string service, WorktreePreviewStartRequest? request, bool startDependencies);

    Task<PreviewResult<WorktreePreviewResponse>> StopServiceAsync(string workItemId, string service);

    Task<PreviewResult<WorktreePreviewServiceConfigResponse>> GetServiceConfigAsync(string workItemId, string service);

    Task<PreviewResult<WorktreePreviewServiceConfigResponse>> UpdateServiceConfigAsync(
        string workItemId, string service, WorktreePreviewServiceConfigUpdateRequest? request);

    Task<PreviewResult<WorktreePreviewLogResponse>> GetLogAsync(string workItemId, string service);
}

public sealed class WorkItemPreview(
    IWorkItemManager workItems,
    IWorktreePreviewService preview,
    IProviderStore providers,
    IPackageFeedResolver feeds,
    IWorkItemNotifier notifier) : IWorkItemPreview
{
    private const string ServiceRequired = "service is required.";

    public Task<PreviewResult<WorktreePreviewResponse>> GetStatusAsync(string workItemId)
        => RunAsync<WorktreePreviewResponse>(workItemId, null, item => preview.GetStatusAsync(item.WorktreePath!));

    public Task<PreviewResult<WorktreePreviewResponse>> StartAsync(string workItemId, WorktreePreviewStartRequest? request)
        => ChangeAsync(workItemId, null, async item =>
        {
            var options = await OptionsAsync(request, item);
            return request?.Wait == false
                ? await preview.BeginStartAsync(item.WorktreePath!, options)
                : await preview.StartAsync(item.WorktreePath!, options);
        });

    public Task<PreviewResult<WorktreePreviewResponse>> StopAsync(string workItemId)
        => ChangeAsync(workItemId, null, item => preview.StopAsync(item.WorktreePath!));

    public Task<PreviewResult<WorktreePreviewResponse>> StartServiceAsync(
        string workItemId, string service, WorktreePreviewStartRequest? request, bool startDependencies)
        => ChangeAsync(workItemId, service, async item =>
        {
            var options = (await OptionsAsync(request, item)) with { StartDependencies = startDependencies };
            return await preview.StartServiceAsync(item.WorktreePath!, service, options);
        });

    public Task<PreviewResult<WorktreePreviewResponse>> StopServiceAsync(string workItemId, string service)
        => ChangeAsync(workItemId, service, item => preview.StopServiceAsync(item.WorktreePath!, service));

    public Task<PreviewResult<WorktreePreviewServiceConfigResponse>> GetServiceConfigAsync(string workItemId, string service)
        => RunAsync<WorktreePreviewServiceConfigResponse>(workItemId, service, async item =>
        {
            var config = await preview.GetServiceConfigAsync(item.WorktreePath!, service);
            return config == null
                ? PreviewResult<WorktreePreviewServiceConfigResponse>.Failed(
                    PreviewRefusal.ConfigNotFound, $"No preview config found for service '{service}'.")
                : PreviewResult<WorktreePreviewServiceConfigResponse>.Ok(new() { Service = service, Config = config });
        });

    public Task<PreviewResult<WorktreePreviewServiceConfigResponse>> UpdateServiceConfigAsync(
        string workItemId, string service, WorktreePreviewServiceConfigUpdateRequest? request)
    {
        if (!string.IsNullOrWhiteSpace(service) && (request == null || string.IsNullOrWhiteSpace(request.Config)))
            return Task.FromResult(PreviewResult<WorktreePreviewServiceConfigResponse>.Failed(PreviewRefusal.Refused, "config is required."));

        return RunAsync<WorktreePreviewServiceConfigResponse>(workItemId, service, async item =>
        {
            await preview.UpdateServiceConfigAsync(item.WorktreePath!, service, request!.Config);
            var config = await preview.GetServiceConfigAsync(item.WorktreePath!, service);
            return new WorktreePreviewServiceConfigResponse { Service = service, Config = config };
        });
    }

    public Task<PreviewResult<WorktreePreviewLogResponse>> GetLogAsync(string workItemId, string service)
        => RunAsync<WorktreePreviewLogResponse>(workItemId, service, async item =>
            new WorktreePreviewLogResponse { Service = service, Content = await preview.GetServiceLogAsync(item.WorktreePath!, service) });

    /// <summary>A start or stop: once it has answered, the work item's open Preview tabs are told to re-read.</summary>
    private Task<PreviewResult<WorktreePreviewResponse>> ChangeAsync(
        string workItemId, string? service, Func<WorkItemView, Task<WorktreePreviewResponse>> change)
        => RunAsync<WorktreePreviewResponse>(workItemId, service, async item =>
        {
            var response = await change(item);
            await notifier.PreviewStateChangedAsync(workItemId);
            return response;
        });

    /// <summary>
    /// The sequence every route shares: the service name (when the route has one), the work item and its worktree,
    /// then <paramref name="call"/> with the preview's refusals caught.
    /// </summary>
    private async Task<PreviewResult<T>> RunAsync<T>(string workItemId, string? service, Func<WorkItemView, Task<T>> call)
        => await RunAsync(workItemId, service, async item => PreviewResult<T>.Ok(await call(item)));

    private async Task<PreviewResult<T>> RunAsync<T>(string workItemId, string? service, Func<WorkItemView, Task<PreviewResult<T>>> call)
    {
        if (service != null && string.IsNullOrWhiteSpace(service))
            return PreviewResult<T>.Failed(PreviewRefusal.Refused, ServiceRequired);

        var item = await workItems.GetWorkItemAsync(workItemId);
        if (item == null)
            return PreviewResult<T>.Failed(PreviewRefusal.WorkItemNotFound);
        if (string.IsNullOrWhiteSpace(item.WorktreePath))
            return PreviewResult<T>.Failed(PreviewRefusal.NoWorktree, "Work item does not currently have an active worktree.");

        try
        {
            return await call(item);
        }
        catch (InvalidOperationException ex)
        {
            return PreviewResult<T>.Failed(PreviewRefusal.Refused, ex.Message);
        }
    }

    /// <summary>
    /// A start's options: the request's own settings plus what the run's repository contributes, its custom
    /// <c>.env</c>, its package feeds' credentials and whose preview data the volumes are.
    /// </summary>
    private async Task<WorktreePreviewStartOptions> OptionsAsync(WorktreePreviewStartRequest? request, WorkItemView workItem)
    {
        var repository = workItem.RunRepositoryId is { } repositoryId ? await providers.GetRepositoryByIdAsync(repositoryId) : null;
        return new(
            request?.ProfileName,
            request?.SkipInstall == true,
            request?.PublicHost,
            request?.PortOverrides,
            repository?.PreviewEnv,
            workItem.Id,
            (await feeds.ResolveAsync(workItem.RunRepositoryId)).Feeds,
            repository is null ? null : new PreviewVolumeScope(repository.Id, repository.KeepPreviewData, workItem.Id, workItem.Title));
    }
}
