using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that mutate state. An agent may create work items in the Backlog
/// column, and edit or delete the items its own session created and change
/// their dependencies — but NOT
/// pre-existing items or items from other sessions. For those it may only
/// propose an edit, which a human approves or rejects (ADR-0022). Agents are
/// still NOT allowed to start, move, or otherwise transition work items via
/// this server.
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
        string[]? tags = null,
        [Description("Optional custom branch name used verbatim by every run of the item, instead of the generated ild/wi-<id>-run-<n> name. Must be a valid git branch name. Omit for the default.")]
        string? branchNameOverride = null,
        [Description("Optional branch every run of the item starts from and opens its PR against, instead of the repository's default branch. Must exist on the remote when the run starts. Omit for the default.")]
        string? baseBranchOverride = null)
    {
        var body = new
        {
            title,
            description,
            repositoryId,
            dependencies,
            createdByLoopRunId = createdByLoopRunId ?? _ild.LoopRunId,
            tags,
            branchNameOverride,
            baseBranchOverride,
        };
        return _ild.PostJsonAsync("api/v1/agent/workitems", body);
    }

    [McpServerTool(Name = "update_workitem")]
    [Description("Edit a work item THIS session created. In a loop run that means an item whose createdByLoopRunId matches the current run (the ILD_LOOP_RUN_ID env var); in a chat session it means an item whose createdByChatSessionId matches the current chat session (the ILD_CHAT_SESSION_ID env var). You CANNOT edit pre-existing items or items created by other runs or sessions; the server rejects those with 403 — use propose_workitem_edit to suggest an edit to one of those for a human to approve. Updates the title and description, and optionally replaces the tags (tags determine which loop template executes the item — each must match a loop template name). Dependencies are changed with add_workitem_dependency and remove_workitem_dependency.")]
    public Task<string> UpdateWorkItem(
        [Description("Work item GUID. Must have been created by this session.")] string id,
        [Description("New title (1..512 chars).")] string title,
        [Description("New description (markdown).")] string description = "",
        [Description("Optional replacement list of tags. Omit to leave tags unchanged.")]
        string[]? tags = null,
        [Description("Optional replacement custom branch name. Omit to leave it unchanged; pass an empty string to go back to the generated per-run branch name. Only the item's next run is affected.")]
        string? branchNameOverride = null,
        [Description("Optional replacement base branch the item's runs start from and open PRs against. Omit to leave it unchanged; pass an empty string to go back to the repository's default branch. Only the item's next run is affected.")]
        string? baseBranchOverride = null)
    {
        var body = new { title, description, tags, branchNameOverride, baseBranchOverride };
        return _ild.PutJsonAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}", body);
    }

    [McpServerTool(Name = "delete_workitem")]
    [Description("Delete a work item THIS session created. In a loop run that means an item whose createdByLoopRunId matches the current run (the ILD_LOOP_RUN_ID env var); in a chat session it means an item whose createdByChatSessionId matches the current chat session (the ILD_CHAT_SESSION_ID env var). You CANNOT delete pre-existing items or items created by other runs or sessions; the server rejects those with 403.")]
    public Task<string> DeleteWorkItem(
        [Description("Work item GUID. Must have been created by this session.")] string id)
        => _ild.DeleteAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}");

    [McpServerTool(Name = "add_workitem_dependency")]
    [Description("Make a work item THIS session created wait on another work item: workItemId will depend on dependsOnWorkItemId. Only workItemId must be this session's own (the same rule as update_workitem); dependsOnWorkItemId may be any existing work item and is not changed. For an item this session did not create the server answers 403 — use propose_workitem_edit with addDependencies to suggest it for a human to approve. An edge that would create a cycle, an unknown item, or an item depending on itself is refused with 400. Adding an edge that already exists changes nothing and says so (changed=false). Returns the item's dependencies after the call, each with id, title and status.")]
    public Task<string> AddWorkItemDependency(
        [Description("GUID of the dependent work item (the one that waits). Must have been created by this session.")] string workItemId,
        [Description("GUID of the work item it should wait on. Any existing work item.")] string dependsOnWorkItemId)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/dependencies",
            new { dependsOnWorkItemId });

    [McpServerTool(Name = "remove_workitem_dependency")]
    [Description("Stop a work item THIS session created from waiting on another work item: removes the edge workItemId -> dependsOnWorkItemId. Only workItemId must be this session's own (the same rule as update_workitem); dependsOnWorkItemId is not changed. For an item this session did not create the server answers 403 — use propose_workitem_edit with removeDependencies to suggest it for a human to approve. Removing an edge that does not exist changes nothing and says so (changed=false). Returns the item's dependencies after the call, each with id, title and status.")]
    public Task<string> RemoveWorkItemDependency(
        [Description("GUID of the dependent work item (the one that waits). Must have been created by this session.")] string workItemId,
        [Description("GUID of the work item it should no longer wait on.")] string dependsOnWorkItemId)
        => _ild.DeleteAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/dependencies/{Uri.EscapeDataString(dependsOnWorkItemId)}");

    [McpServerTool(Name = "propose_workitem_edit")]
    [Description("Propose an edit to ANY work item, including ones this session did not create. Nothing changes until a human approves the proposal; they may instead reject it, with a reason. Only the fields you pass are proposed — omit a field to leave it out of the proposal. If the item is edited before the human approves, the proposal goes stale and nothing is applied; propose again against the current values if the edit still makes sense. Returns the proposal id and its status (Pending). Use list_workitem_edit_proposals to see what became of it. A proposal can also add or remove dependencies of the item; one that changes them goes stale if the item's dependencies change before approval, and an approval whose addition would by then create a cycle is refused and stays pending. For items this session created, update_workitem, add_workitem_dependency and remove_workitem_dependency apply changes directly.")]
    public Task<string> ProposeWorkItemEdit(
        [Description("Work item GUID. Any work item.")] string id,
        [Description("Proposed title (1..512 chars). Omit to leave the title out of the proposal.")]
        string? title = null,
        [Description("Proposed description (markdown). Omit to leave the description out of the proposal.")]
        string? description = null,
        [Description("Proposed replacement list of tags. Tags that name a loop template select the loop that runs the item; other tags are labels loops may read (HIL, for one). Omit to leave tags out of the proposal.")]
        string[]? tags = null,
        [Description("Proposed custom branch name used verbatim by every run of the item. Must be a valid git branch name. Pass an empty string to propose going back to the generated per-run branch name; omit to leave it out of the proposal.")]
        string? branchNameOverride = null,
        [Description("Proposed base branch the item's runs start from and open PRs against. Pass an empty string to propose going back to the repository's default branch; omit to leave it out of the proposal.")]
        string? baseBranchOverride = null,
        [Description("Proposed work item GUIDs for the item to start depending on. Each must exist, not already be a dependency, and not create a cycle. Omit to add none.")]
        string[]? addDependencies = null,
        [Description("Proposed work item GUIDs for the item to stop depending on. Each must be a current dependency. Omit to remove none.")]
        string[]? removeDependencies = null,
        [Description("Optional short reason for the edit (up to 2000 chars), shown to the human deciding.")]
        string? rationale = null)
    {
        var body = new { title, description, tags, branchNameOverride, baseBranchOverride, addDependencies, removeDependencies, rationale };
        return _ild.PostJsonAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}/edit-proposals", body);
    }

    [McpServerTool(Name = "list_workitem_edit_proposals")]
    [Description("List the edit proposals on a work item, newest first: each one's status (Pending, Approved, Rejected or Stale), the proposed and snapshot values, and a rejection's reason. Read-only — only a human can approve or reject a proposal.")]
    public Task<string> ListWorkItemEditProposals(
        [Description("Work item GUID.")] string id)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}/edit-proposals");
}
