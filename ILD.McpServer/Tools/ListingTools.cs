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
    [Description("List work items, optionally filtered. Status is one of Backlog, WorkQueue, Ready, Running, HumanFeedback, Done. Use createdByLoopRunId to find items spawned by a specific loop run (useful for cleaning up after a rogue agent).")]
    public Task<string> ListWorkItems(
        [Description("Optional status filter: Backlog, WorkQueue, Ready, Running, HumanFeedback, Done.")]
        string? status = null,
        [Description("Optional repository GUID to filter by.")]
        string? repositoryId = null,
        [Description("Optional originating LoopRun GUID to filter by.")]
        string? createdByLoopRunId = null,
        [Description("Pagination skip (default 0).")] int skip = 0,
        [Description("Pagination take (default 100, max 500).")] int take = 100)
    {
        var qs = BuildQuery(("status", status), ("repositoryId", repositoryId),
                            ("createdByLoopRunId", createdByLoopRunId),
                            ("skip", skip.ToString()), ("take", take.ToString()));
        return _ild.GetRawAsync($"api/v1/agent/workitems{qs}");
    }

    [McpServerTool(Name = "get_workitem")]
    [Description("Get a single work item by id, including its dependencies.")]
    public Task<string> GetWorkItem([Description("Work item GUID.")] string id)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}");

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
