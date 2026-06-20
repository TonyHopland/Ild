using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that mutate state. An agent may create work items in the Backlog
/// column, and edit or delete the items its own session created — but NOT
/// pre-existing items or items from other sessions. Agents are still NOT
/// allowed to start, move, or otherwise transition work items via this server.
/// </summary>
[McpServerToolType]
public sealed class WorkItemTools
{
    private readonly IldClient _ild;

    public WorkItemTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "create_workitem")]
    [Description("Create a new work item in the Backlog column. The new item is stamped with the originating loop-run id (read from the ILD_LOOP_RUN_ID env var unless overridden) so a user can later batch-clean items if the agent goes rogue. You MUST call list_repositories first and pass a valid repositoryId — it is required. Dependencies must reference existing work item ids; cycles are rejected by the server. Tags determine which loop template executes the work item — each tag must match a loop template name.")]
    public Task<string> CreateWorkItem(
        [Description("Title (1..512 chars).")] string title,
        [Description("Required Repository GUID. Use list_repositories to discover ids.")] string repositoryId,
        [Description("Description (markdown).")] string description = "",
        [Description("Optional list of WorkItem GUIDs this item depends on.")]
        string[]? dependencies = null,
        [Description("Optional originating LoopRun GUID. Defaults to the ILD_LOOP_RUN_ID env var.")]
        string? createdByLoopRunId = null,
        [Description("Optional list of tags. Each tag determines which loop template executes the work item — a tag must match a loop template name on the ILD instance.")]
        string[]? tags = null)
    {
        var body = new
        {
            title,
            description,
            repositoryId,
            dependencies,
            createdByLoopRunId = createdByLoopRunId ?? _ild.LoopRunId,
            tags,
        };
        return _ild.PostJsonAsync("api/v1/agent/workitems", body);
    }

    [McpServerTool(Name = "update_workitem")]
    [Description("Edit a work item THIS session created. In a loop run that means an item whose createdByLoopRunId matches the current run (the ILD_LOOP_RUN_ID env var); in a chat session it means an item whose createdByChatSessionId matches the current chat session (the ILD_CHAT_SESSION_ID env var). You CANNOT edit pre-existing items or items created by other runs or sessions; the server rejects those with 403. Updates the title and description, and optionally replaces the tags (tags determine which loop template executes the item — each must match a loop template name).")]
    public Task<string> UpdateWorkItem(
        [Description("Work item GUID. Must have been created by this session.")] string id,
        [Description("New title (1..512 chars).")] string title,
        [Description("New description (markdown).")] string description = "",
        [Description("Optional replacement list of tags. Omit to leave tags unchanged.")]
        string[]? tags = null)
    {
        var body = new { title, description, tags };
        return _ild.PutJsonAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}", body);
    }

    [McpServerTool(Name = "delete_workitem")]
    [Description("Delete a work item THIS session created. In a loop run that means an item whose createdByLoopRunId matches the current run (the ILD_LOOP_RUN_ID env var); in a chat session it means an item whose createdByChatSessionId matches the current chat session (the ILD_CHAT_SESSION_ID env var). You CANNOT delete pre-existing items or items created by other runs or sessions; the server rejects those with 403.")]
    public Task<string> DeleteWorkItem(
        [Description("Work item GUID. Must have been created by this session.")] string id)
        => _ild.DeleteAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}");
}
