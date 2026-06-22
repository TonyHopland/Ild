using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that drive a work item's Worktree Preview (ADR-0011), mirroring the
/// human work item dialog's Preview tab. Every tool takes an explicit
/// <c>workItemId</c> — the agent reads the open work item id from its Chat Context
/// and passes it, just like <see cref="ListingTools"/>'s <c>get_workitem</c>.
///
/// These fold under the same <c>ild</c> grant as the other tools. They are scoped
/// to the agent-API preview surface (<c>/api/v1/agent/workitems/{id}/preview...</c>),
/// which itself requires the work item to have an active worktree.
///
/// Drift warning: these names and shapes must stay in lockstep with the Pi
/// surface (<see cref="ILD.Data.ToolDescriptors"/>) and the agent-API endpoints
/// (<c>AgentController</c>) so the chat behaves the same whichever CLI backs it.
/// </summary>
[McpServerToolType]
public sealed class PreviewTools
{
    private readonly IldClient _ild;

    public PreviewTools(IldClient ild) { _ild = ild; }

    private static string Wi(string workItemId) => Uri.EscapeDataString(workItemId);
    private static string Svc(string service) => Uri.EscapeDataString(service);

    [McpServerTool(Name = "get_preview")]
    [Description("Get the worktree preview status for a work item: whether it is configured, the resolved profile, and each service's state (port, health URL, public URL, process/exit info).")]
    public Task<string> GetPreview([Description("Work item GUID (from the Chat Context).")] string workItemId)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview");

    [McpServerTool(Name = "start_preview")]
    [Description("Start the worktree preview for a work item — runs the profile's install steps (unless skipInstall) and starts every service. Returns the updated preview status.")]
    public Task<string> StartPreview(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Optional profile name; defaults to the config's default profile.")] string? profileName = null,
        [Description("Skip the install steps (default false).")] bool skipInstall = false)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/start",
            new { profileName, skipInstall });

    [McpServerTool(Name = "stop_preview")]
    [Description("Stop the worktree preview for a work item — tears down all running services. Returns the updated preview status.")]
    public Task<string> StopPreview([Description("Work item GUID (from the Chat Context).")] string workItemId)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/stop", new { });

    [McpServerTool(Name = "start_preview_service")]
    [Description("Start a single preview service by name, leaving the others untouched. The first service started provisions the shared runtime. Returns the updated preview status.")]
    public Task<string> StartPreviewService(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("Optional profile name; defaults to the config's default profile.")] string? profileName = null,
        [Description("Skip the install steps (default false).")] bool skipInstall = false)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/start",
            new { profileName, skipInstall });

    [McpServerTool(Name = "stop_preview_service")]
    [Description("Stop a single running preview service by name, leaving the others running. Returns the updated preview status.")]
    public Task<string> StopPreviewService(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Service name as declared in ild.config.json.")] string service)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/stop", new { });

    [McpServerTool(Name = "get_preview_service_config")]
    [Description("Get one service's entry from the worktree's ild.config.json as raw JSON so it can be inspected or edited in place.")]
    public Task<string> GetPreviewServiceConfig(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Service name as declared in ild.config.json.")] string service)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/config");

    [McpServerTool(Name = "update_preview_service_config")]
    [Description("Replace one service's entry in the worktree's ild.config.json with the supplied JSON. The JSON is validated with the same rules as preview start and its 'name' must match the service. Takes effect the next time the service is started.")]
    public Task<string> UpdatePreviewServiceConfig(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Service name as declared in ild.config.json.")] string service,
        [Description("The full service object as JSON.")] string config)
        => _ild.PutJsonAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/services/{Svc(service)}/config",
            new { config });

    [McpServerTool(Name = "get_preview_logs")]
    [Description("Read the tail of a preview service's captured stdout/stderr log — useful to see why a service failed to start. Returns null content when the preview was never started.")]
    public Task<string> GetPreviewLogs(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Service name as declared in ild.config.json.")] string service)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Wi(workItemId)}/preview/logs?service={Uri.EscapeDataString(service)}");
}
