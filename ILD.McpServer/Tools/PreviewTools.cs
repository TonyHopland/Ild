using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that drive a work item's Worktree Preview (ADR-0011), mirroring the
/// human work item dialog's Preview tab. Every tool takes an optional
/// <c>workItemId</c>. A loop run's AI node omits it and gets the run's own work item;
/// a chat reads the open work item id from its Chat Context and passes it, just like
/// <see cref="ListingTools"/>'s <c>get_workitem</c>.
///
/// These fold under the same <c>ild</c> grant as the other tools. They are scoped
/// to the agent-API preview surface (<c>/api/v1/agent/workitems/{id}/preview...</c>),
/// which itself requires the work item to have an active worktree.
///
/// Drift warning: these shapes must stay in lockstep with the agent-API endpoints
/// (<c>AgentController</c>) they call.
/// </summary>
[McpServerToolType]
public sealed class PreviewTools
{
    private readonly IldClient _ild;

    public PreviewTools(IldClient ild) { _ild = ild; }

    private string Wi(string? workItemId)
    {
        var id = string.IsNullOrWhiteSpace(workItemId) ? _ild.WorkItemId : workItemId;
        if (string.IsNullOrWhiteSpace(id))
            throw new McpException("No workItemId given and this session has no work item of its own; pass the work item GUID.");
        return Uri.EscapeDataString(id);
    }
    private static string Svc(string service) => Uri.EscapeDataString(service);

    [McpServerTool(Name = "get_preview")]
    [Description("Get the worktree preview status for a work item: whether it is configured, the resolved profile, and each service's state (port, health URL, public URL, local URL, agentUrl, process/exit info). To open a service yourself, use agentUrl: it is the address that works from where you run — http://127.0.0.1:<port> inside this work item's own run, otherwise http://wi-<id>.preview.ild/, which your HTTP proxy (HTTP_PROXY, which ILD sets) carries to ILD; a *.localhost URL is your own machine, not ILD. While a start is running the state is \"installing\" (with the install step) or \"starting\", and each service reads pending, starting, waitingForHealth, running or failed (with its exit code and error); a failed start keeps that failure until the preview or one of its services is next started or stopped.")]
    public Task<string> GetPreview([Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview");

    [McpServerTool(Name = "start_preview")]
    [Description("Start the worktree preview for a work item — runs the profile's install steps (unless skipInstall) and starts every service. By default it waits until every service has settled and returns the updated preview status (open a service at its agentUrl, the address that works from where you run; localUrl is http://127.0.0.1:<port>); with wait=false it returns once the start has begun, and the start is followed with get_preview. If a start is already in progress this call follows that one, with its profile and options, instead of starting another.")]
    public Task<string> StartPreview(
        [Description("Optional profile name; defaults to the config's default profile.")] string? profileName = null,
        [Description("Skip the install steps (default false).")] bool skipInstall = false,
        [Description("Wait until every service is up (default true). false returns once the start has begun; follow it with get_preview.")] bool wait = true,
        [Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/start",
            new { profileName, skipInstall, wait });

    [McpServerTool(Name = "stop_preview")]
    [Description("Stop the worktree preview for a work item — tears down all running services. Returns the updated preview status.")]
    public Task<string> StopPreview([Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/stop", new { });

    [McpServerTool(Name = "start_preview_service")]
    [Description("Start a single preview service by name, leaving the others untouched. The first service started provisions the shared runtime. Returns the updated preview status.")]
    public Task<string> StartPreviewService(
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("Optional profile name; defaults to the config's default profile.")] string? profileName = null,
        [Description("Skip the install steps (default false).")] bool skipInstall = false,
        [Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/start",
            new { profileName, skipInstall });

    [McpServerTool(Name = "stop_preview_service")]
    [Description("Stop a single running preview service by name, leaving the others running. Returns the updated preview status.")]
    public Task<string> StopPreviewService(
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/stop", new { });

    [McpServerTool(Name = "get_preview_service_config")]
    [Description("Get one service's entry from the worktree's ild.config.json as raw JSON so it can be inspected or edited in place.")]
    public Task<string> GetPreviewServiceConfig(
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/config");

    [McpServerTool(Name = "update_preview_service_config")]
    [Description("Replace one service's entry in the worktree's ild.config.json with the supplied JSON. The JSON is validated with the same rules as preview start and its 'name' must match the service. Takes effect the next time the service is started.")]
    public Task<string> UpdatePreviewServiceConfig(
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("The full service object as JSON.")] string config,
        [Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.PutJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/config",
            new { config });

    [McpServerTool(Name = "get_preview_logs")]
    [Description("Read the tail of a preview service's captured stdout/stderr log — useful to see why a service failed to start. Returns null content when the preview was never started.")]
    public Task<string> GetPreviewLogs(
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("Work item GUID. Defaults to this run's work item.")] string? workItemId = null)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/logs?service={Uri.EscapeDataString(service)}");
}
