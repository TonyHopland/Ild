using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that mutate state. Deliberately limited to creating new work
/// items in the Backlog column. Agents are NOT allowed to start, move, or
/// otherwise transition work items via this server.
/// </summary>
[McpServerToolType]
public sealed class WorkItemTools
{
    private readonly IldClient _ild;

    public WorkItemTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "create_workitem")]
    [Description("Create a new work item in the Backlog column. The new item is stamped with the originating loop-run id (read from the ILD_LOOP_RUN_ID env var unless overridden) so a user can later batch-clean items if the agent goes rogue. You MUST call list_repositories first and pass a valid repositoryId — it is required. Optionally pass a loopTemplateId from list_loop_templates; the latest version is pinned at first run start. Dependencies must reference existing work item ids; cycles are rejected by the server.")]
    public Task<string> CreateWorkItem(
        [Description("Title (1..512 chars).")] string title,
        [Description("Required Repository GUID. Use list_repositories to discover ids.")] string repositoryId,
        [Description("Description (markdown, up to 4096 chars).")] string description = "",
        [Description("Optional LoopTemplate GUID; the latest version is pinned at first run start.")]
        string? loopTemplateId = null,
        [Description("Optional list of WorkItem GUIDs this item depends on.")]
        string[]? dependencies = null,
        [Description("Optional originating LoopRun GUID. Defaults to the ILD_LOOP_RUN_ID env var.")]
        string? createdByLoopRunId = null)
    {
        var body = new
        {
            title,
            description,
            loopTemplateId,
            repositoryId,
            dependencies,
            createdByLoopRunId = createdByLoopRunId ?? _ild.LoopRunId,
        };
        return _ild.PostJsonAsync("api/v1/agent/workitems", body);
    }
}
