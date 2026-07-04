using System.ComponentModel;
using System.Text;
using System.Web;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that let an agent inspect the ILD platform: list work items,
/// repositories, loop templates, and loop runs.
///
/// All tools are read-only. The companion <see cref="WorkItemTools"/> exposes
/// the single mutating operation: creating a work item in Backlog. Agents
/// cannot start, transition, or otherwise move work items via this server.
/// </summary>
[McpServerToolType]
public sealed class ListingTools
{
    private readonly IldClient _ild;

    public ListingTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "list_workitems")]
    [Description("List work items as lightweight, relationship-aware rows for triage — NO full bodies. Each row carries title, status, priority, tags, a short descriptionPreview, the dependency ids blocking it (blockedBy/blockedByCount), the reverse-edge count of items it blocks (blocksCount), and an actionable flag (true when all its dependencies are Done). Use get_backlog_summary first to orient, then this to shortlist, then get_workitem for the few you pick. Status is one of Backlog, WorkQueue, Ready, Running, HumanFeedback, Done. Use createdByLoopRunId to find items spawned by a specific loop run (useful for cleaning up after a rogue agent).")]
    public Task<string> ListWorkItems(
        [Description("Optional status filter: Backlog, WorkQueue, Ready, Running, HumanFeedback, Done.")]
        string? status = null,
        [Description("Optional repository GUID to filter by.")]
        string? repositoryId = null,
        [Description("Optional originating LoopRun GUID to filter by.")]
        string? createdByLoopRunId = null,
        [Description("Optional priority filter: Low, Medium, High, Critical.")]
        string? priority = null,
        [Description("Optional tags to filter by; an item matches if it carries any of them.")]
        string[]? tags = null,
        [Description("Sort order: updatedAt (default), createdAt, or priority (highest first). Timestamp orders are most-recent first.")]
        string? orderBy = null,
        [Description("When true, return only items whose dependencies are all Done (actionable now).")]
        bool actionableOnly = false,
        [Description("When true, also include each item's full description body (off by default to keep the list lightweight).")]
        bool includeDescription = false,
        [Description("Pagination skip (default 0).")] int skip = 0,
        [Description("Pagination take (default 100, max 500).")] int take = 100)
    {
        var qs = BuildQuery(("status", status), ("repositoryId", repositoryId),
                            ("createdByLoopRunId", createdByLoopRunId),
                            ("priority", priority), ("orderBy", orderBy),
                            ("actionableOnly", actionableOnly ? "true" : null),
                            ("includeDescription", includeDescription ? "true" : null),
                            ("skip", skip.ToString()), ("take", take.ToString()));
        qs = AppendRepeated(qs, "tags", tags);
        return _ild.GetRawAsync($"api/v1/agent/workitems{qs}");
    }

    [McpServerTool(Name = "get_backlog_summary")]
    [Description("Orient over a backlog without pulling any bodies: returns total count, counts by status, counts by priority, and how many items are blocked vs. actionable (dependencies all Done). Call this first when triaging a large backlog, then narrow with list_workitems.")]
    public Task<string> GetBacklogSummary(
        [Description("Optional repository GUID to scope the summary to.")] string? repositoryId = null)
    {
        var qs = BuildQuery(("repositoryId", repositoryId));
        return _ild.GetRawAsync($"api/v1/agent/workitems/summary{qs}");
    }

    [McpServerTool(Name = "get_workitem")]
    [Description("Get a single work item's full record: full description body, dependencies and reverse 'blocks' edges resolved to {id,title,status}. The conversation is excluded by default (it is the largest field); pass includeConversation=true to include it.")]
    public Task<string> GetWorkItem(
        [Description("Work item GUID.")] string id,
        [Description("Include the conversation thread (large; default false).")] bool includeConversation = false)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}?includeConversation={(includeConversation ? "true" : "false")}");

    [McpServerTool(Name = "list_repositories")]
    [Description("List repositories the agent can attach a work item to.")]
    public Task<string> ListRepositories(
        [Description("Pagination skip (default 0).")] int skip = 0,
        [Description("Pagination take (default 100, max 500).")] int take = 100)
        => _ild.GetRawAsync($"api/v1/agent/repositories?skip={skip}&take={take}");

    [McpServerTool(Name = "list_loop_templates")]
    [Description("List loop templates available for new work items.")]
    public Task<string> ListLoopTemplates(
        [Description("Pagination skip (default 0).")] int skip = 0,
        [Description("Pagination take (default 100, max 500).")] int take = 100,
        [Description("Include archived templates (default false).")] bool includeArchived = false)
        => _ild.GetRawAsync($"api/v1/agent/loop-templates?skip={skip}&take={take}&includeArchived={(includeArchived ? "true" : "false")}");

    [McpServerTool(Name = "list_loop_runs")]
    [Description("List loop runs. Pass workItemId to scope to a specific work item. Use the returned run id to find work items the run created via list_workitems(createdByLoopRunId=...).")]
    public Task<string> ListLoopRuns(
        [Description("Optional WorkItem GUID to scope by.")] string? workItemId = null,
        [Description("Pagination skip (default 0).")] int skip = 0,
        [Description("Pagination take (default 100, max 500).")] int take = 100)
    {
        var qs = BuildQuery(("workItemId", workItemId), ("skip", skip.ToString()), ("take", take.ToString()));
        return _ild.GetRawAsync($"api/v1/agent/loop-runs{qs}");
    }

    /// <summary>
    /// Append a repeated query parameter (<c>?key=a&amp;key=b</c>) for each
    /// non-empty value, continuing whatever query string <paramref name="qs"/>
    /// already holds.
    /// </summary>
    private static string AppendRepeated(string qs, string key, string[]? values)
    {
        if (values is null) return qs;
        var sb = new StringBuilder(qs);
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            sb.Append(sb.Length == 0 ? '?' : '&');
            sb.Append(HttpUtility.UrlEncode(key));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(v));
        }
        return sb.ToString();
    }

    private static string BuildQuery(params (string Key, string? Value)[] parts)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (k, v) in parts)
        {
            if (string.IsNullOrEmpty(v)) continue;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(HttpUtility.UrlEncode(k));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(v));
        }
        return sb.ToString();
    }
}
