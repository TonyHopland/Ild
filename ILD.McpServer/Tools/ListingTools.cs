using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Web;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that let an agent inspect the ILD platform: list work items,
/// repositories, loop templates, and loop runs; read a saved template version,
/// one run's path and feedback, and the per-template analytics, so an agent can
/// review how loops performed.
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
    [Description("Get a single work item's full record: full description body, dependencies and reverse 'blocks' edges resolved to {id,title,status}. The conversation is excluded by default (it is the largest field); pass includeConversation=true to include the latest run's conversation, projected from that run's event log, as {runId, messages}. Each message carries its run id, event id, runNodeId, role, name, text and timestamp, in event order; earlier runs are listed by list_loop_runs. Its change history is excluded too; pass includeHistory=true to include it as history, newest first: one entry per changed field, with saveId (shared by one save's changes), timestamp, kind (Created, Baseline or Changed), username, actorKind, actorDetail, actorWorkItemId (the work item of the loop run that made the change), field, oldValue, newValue and reason.")]
    public Task<string> GetWorkItem(
        [Description("Work item GUID.")] string id,
        [Description("Include the latest run's conversation, projected from its event log, with run id, event id and timestamp per message (large; default false).")] bool includeConversation = false,
        [Description("Include the item's change history: who changed which field, from what to what, and why (default false).")] bool includeHistory = false)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(id)}?includeConversation={(includeConversation ? "true" : "false")}&includeHistory={(includeHistory ? "true" : "false")}");

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
    [Description("List loop runs, newest first by start time (creation time for a run not yet started). Each row carries the run's template id, name and version number. Pass workItemId to scope to a specific work item, from/to for a date range and templateId for one template; the filters combine. Use the returned run id with get_loop_run for its path and feedback, or to find work items the run created via list_workitems(createdByLoopRunId=...).")]
    public Task<string> ListLoopRuns(
        [Description("Optional WorkItem GUID to scope by.")] string? workItemId = null,
        [Description("Optional first day, yyyy-MM-dd (UTC, inclusive), on the run's start day.")] string? from = null,
        [Description("Optional last day, yyyy-MM-dd (UTC, inclusive), on the run's start day.")] string? to = null,
        [Description("Optional loop template GUID to scope by (any of its versions).")] string? templateId = null,
        [Description("Pagination skip (default 0).")] int skip = 0,
        [Description("Pagination take (default 100, max 500).")] int take = 100)
    {
        var qs = BuildQuery(("workItemId", workItemId), ("from", from), ("to", to), ("templateId", templateId),
                            ("skip", skip.ToString()), ("take", take.ToString()));
        return _ild.GetRawAsync($"api/v1/agent/loop-runs{qs}");
    }

    [McpServerTool(Name = "get_loop_run")]
    [Description("Read one loop run as a summary (read-only): status, template version, work item, timings; the node visits in execution order, each with its outcome, duration, error, tokens, cost, the edge it was entered by (incomingEdge) and the edge it took (edgeTaken: the edge a later visit that came from this one entered by, when that edge leaves this node; for the last visit, if it completed last, the edge the run has followed from it toward its current node; otherwise null); the human-feedback requests and answers, and the run's parks (kind parked, which also covers ILD's own waits such as an AI provider throttle; text gives the reason); the AI traversal and throttle counters; and the pull request with its merge state and the review comments from the last stored PR snapshot (fetchedAt says how old). Long texts are cut at 2,000 characters and flagged (errorTruncated, textTruncated, bodyTruncated). No AI transcripts or node outputs. Node ids match get_loop_template's document.")]
    public Task<string> GetLoopRun(
        [Description("Loop run GUID (from list_loop_runs).")] string runId)
        => _ild.GetRawAsync($"api/v1/agent/loop-runs/{Uri.EscapeDataString(runId)}");

    [McpServerTool(Name = "get_loop_template")]
    [Description("Read a saved loop template version (read-only) as an ild-loop-template/v2 document, the same format get_current_loop returns, so it can be compared with or handed to update_current_loop. Returns { templateId, version, latestVersion, isLatest, versionCreatedAt, isArchived, document }. The document's nodes, edges and configs are that version's; its name, description and recoveryPolicy are the template's current values, because they are not versioned. Node ids match the nodeId values from get_loop_run and get_template_analytics. A name shared by several templates is refused with the candidates' ids.")]
    public Task<string> GetLoopTemplate(
        [Description("Loop template GUID, or its exact name (case-insensitive; archived templates included). A GUID that is no template's id is tried as a name.")] string nameOrId,
        [Description("Optional version number; default the latest.")] int? version = null)
    {
        var qs = BuildQuery(("nameOrId", nameOrId), ("version", version?.ToString(CultureInfo.InvariantCulture)));
        return _ild.GetRawAsync($"api/v1/agent/loop-templates/document{qs}");
    }

    [McpServerTool(Name = "get_template_analytics")]
    [Description("Per-loop-template performance (read-only), the figures the analytics dashboard shows: total, completed, failed and cancelled runs, success rate, average seconds per node, on-failure and reject routings, average human-feedback wait, tokens and cost — deleted runs included. Each template also lists per-node figures (executions, failures, avgSeconds) keyed by node id and template version; those come only from runs that still exist, and runsWithNodeDetail says how many. Templates are ordered costliest first.")]
    public Task<string> GetTemplateAnalytics(
        [Description("Optional first day, yyyy-MM-dd (UTC, inclusive), on the run's start day.")] string? from = null,
        [Description("Optional last day, yyyy-MM-dd (UTC, inclusive), on the run's start day.")] string? to = null,
        [Description("Optional agent provider name (the run's primary AI provider, case-insensitive).")] string? provider = null,
        [Description("Optional loop template GUID to report on alone.")] string? templateId = null)
    {
        var qs = BuildQuery(("from", from), ("to", to), ("provider", provider), ("templateId", templateId));
        return _ild.GetRawAsync($"api/v1/agent/analytics/templates{qs}");
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
