using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Api.Authentication;
using ILD.Api.Configuration;
using ILD.Api.Contracts;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// Read-only / restricted API surface intended for the ILD MCP server.
///
/// Who is calling comes from the caller's token, never from anything it sends: ILD issues one token per run and per
/// chat launch, revokes it when the launch ends, and carries the run or chat id on the principal as a claim. A
/// <c>X-ILD-Run-Id</c> or <c>X-ILD-Chat-Session-Id</c> header naming another run or chat is refused (403), and a user
/// has neither. A run's token reaches only its own run's work item and the items that run created for the worktree,
/// preview, pull request, conversation and attachment routes; a chat's token, driven by the operator, reaches any work
/// item but is bound to its own chat and cannot act as a run.
///
/// The MCP server lets agents inside loop runs:
///  - list work items (with filters, including by originating run),
///  - list loop templates and repositories so they can pick valid ids,
///  - list loop runs so users can identify and batch-delete items
///    spawned by a specific run if an agent goes rogue,
///  - create new work items into Backlog with an optional set of
///    dependencies, stamped with the originating loop-run id,
///  - edit or delete work items the caller's own session created, and add
///    or remove their dependencies — the item's <c>CreatedByLoopRunId</c>
///    (or <c>CreatedByChatSessionId</c>) must match the caller's session;
///    pre-existing items and items from other sessions are off-limits (403),
///  - propose an edit to ANY work item (a Work Item Edit Proposal, ADR-0022)
///    and read back what became of it. Proposing applies nothing: a human
///    approves or rejects each proposal on the human-only
///    <c>WorkItemsController</c>, and no route here can decide one,
///  - read the global variables and suggest a whole new value for one (a
///    Global Variable Proposal, ADR-0027), and read back what became of the
///    caller's own suggestions. Only a human decides a suggestion, on the
///    human-only <c>GlobalVariablesController</c>,
///  - propose a change to a saved loop template, as a whole document or an
///    Edit List, against its latest version (a Loop Template Proposal,
///    ADR-0028), and read back what became of the caller's own proposals.
///    Only a human saves or rejects one, on the human-only
///    <c>LoopTemplateProposalsController</c>,
///  - serve an external-agent grant (ADR-0037) as a session of its own: it creates, edits and proposes like a run or chat, and reaches the
///    worktree, preview, pull request and attachment routes only for the items it created. It has no run or chat, so the loop-variable,
///    current-loop and chat-clone routes refuse it,
///  - read and write per-run loop variables (scoped by the run's token) so one node can hand off state to a later node — the values
///    are also exposed to templates as <c>{{Var.&lt;name&gt;}}</c>,
///  - pull commits pushed to their own run branch, which needs the
///    orchestrator's repository token the agent uid cannot reach (ADR-0014).
///  - in a chat a schedule started, have the orchestrator clone or update a
///    repository the schedule allows into the chat's scratch folder, with
///    credentials that never reach the clone (ADR-0014).
///
/// Crucially, this controller deliberately does NOT expose transitions
/// (other than into the work queue), link-pr, or merge. Agents are not
/// allowed to move work items out of Backlog; that gate is human-only,
/// with one exception: an external-agent grant at the Control level (ADR-0039)
/// may start work items, queue them, reorder them within their status and answer human feedback. Run and chat
/// agents never reach those routes.
/// </summary>
[ApiController]
[Route("api/v1/agent")]
// An opt-out from the user-only fallback policy: this is the whole surface
// agents may reach: ILD's own, and external-agent grants. The UI calls it too, hence agent *or* user.
[Authorize(Policy = IldAuthentication.AgentSurfacePolicy)]
public class AgentController : ControllerBase, IActionFilter
{
    // What the MCP server used to send. The caller's run or chat comes from its token; a header that names another is refused.
    private const string RunIdHeader = "X-ILD-Run-Id";
    private const string ChatSessionIdHeader = "X-ILD-Chat-Session-Id";

    /// <summary>
    /// An agent's token names its run or chat, so a header naming a different one (or a run header from a chat, or the
    /// reverse) is a forgery attempt and is refused. A header that agrees changes nothing, and a user's are ignored.
    /// </summary>
    [NonAction]
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (User.IsInRole(IldAuthentication.ExternalAgentRole))
        {
            context.Result = RefuseGrantSessionHeaders() ?? RefuseGrantWrite();
            return;
        }
        if (!User.IsInRole(IldAuthentication.AgentRole))
            return;
        if (HeaderDisagrees(RunIdHeader, AgentTokenSource.Run) || HeaderDisagrees(ChatSessionIdHeader, AgentTokenSource.Chat))
            context.Result = StatusCode(403, new { error = "The run or chat id sent does not belong to this agent token." });
    }

    [NonAction]
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /// <summary>A grant is its own session: a header naming a run or a chat is refused, never honoured.</summary>
    private IActionResult? RefuseGrantSessionHeaders()
        => HasHeader(RunIdHeader) || HasHeader(ChatSessionIdHeader)
            ? StatusCode(403, new { error = "An external-agent grant is its own session and cannot act as a run or a chat; remove the X-ILD-Run-Id and X-ILD-Chat-Session-Id headers." })
            : null;

    private IActionResult? RefuseGrantWrite()
        => User.FindFirstValue(IldAuthentication.GrantScopeClaim) == nameof(ExternalAgentGrantScope.ReadOnly)
           && !HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method)
            ? StatusCode(403, new { error = "This grant is read-only: it can read but not create work items, make proposals or control work." })
            : null;

    private bool HasHeader(string header)
        => Request.Headers.TryGetValue(header, out var sent) && !string.IsNullOrWhiteSpace(sent.ToString());

    private bool HeaderDisagrees(string header, AgentTokenSource source)
        => Request.Headers.TryGetValue(header, out var sent)
           && !string.IsNullOrWhiteSpace(sent.ToString())
           && !(Caller.SessionClaim(source) is { } own && Guid.TryParse(sent.ToString(), out var named) && named == own);

    private readonly IWorkItemManager _workItems;
    private readonly ILoopTemplateManager _templates;
    private readonly ILoopRunStore _runs;
    private readonly AppDbContext _db;
    private readonly IWorkItemPreview _preview;
    private readonly IChatLoopScratchpad _loopScratchpad;
    private readonly IChatNotifier _chatNotifier;
    private readonly IAgentSessionResolver _sessions;

    public AgentController(
        IWorkItemManager workItems,
        ILoopTemplateManager templates,
        ILoopRunStore runs,
        AppDbContext db,
        IWorkItemPreview preview,
        IChatLoopScratchpad loopScratchpad,
        IChatNotifier chatNotifier,
        IAgentSessionResolver sessions)
    {
        _workItems = workItems;
        _templates = templates;
        _runs = runs;
        _db = db;
        _preview = preview;
        _loopScratchpad = loopScratchpad;
        _chatNotifier = chatNotifier;
        _sessions = sessions;
    }

    /// <summary>
    /// Resolve a work item and its current worktree path for the preview surface,
    /// mirroring the human <c>WorkItemsController</c> gate: 404 when the item is
    /// unknown, 400 when it has no active worktree. The agent reads the open work
    /// item id from its Chat Context and passes it explicitly (consistent with
    /// <c>get_workitem</c>).
    /// </summary>
    private async Task<(WorkItemView? WorkItem, IActionResult? Error)> GetPreviewableWorkItemAsync(string id)
    {
        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return (null, NotFound());
        if (string.IsNullOrWhiteSpace(workItem.WorktreePath))
            return (null, BadRequest(new { error = "Work item does not currently have an active worktree." }));
        return (workItem, null);
    }


    // Single-line description preview length for the lightweight list (~160
    // chars per the triage design): enough to recognise an item, far short of
    // its full body.
    private const int DescriptionPreviewLength = 160;

    // Upper bound on the number of dependency ids returned per row. blockedByCount
    // stays exact; only the id array is capped so a pathological item with hundreds
    // of dependencies can't bloat the (per-page) list payload.
    private const int MaxBlockedByIds = 50;

    /// <summary>
    /// What an AI handed a grant link reads first. Open to everyone and secret-free: it names the surface and how to
    /// authenticate to it, and the operator's token is what opens the rest.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("_about")]
    public IActionResult About([FromServices] IConfiguration configuration, [FromServices] TrustedProxies proxies)
    {
        var baseUrl = PublicUrl.For(Request, configuration, proxies);
        return Ok(new
        {
            name = "ILD agent API",
            description = "ILD coordinates coding agents over a backlog of work items. A grant gives an AI session outside ILD temporary access to the same tools ILD's own agents use. It ends when it expires or the operator revokes it.",
            authentication = "Send the grant token on every request as the header 'Authorization: Bearer <token>'. A token in a query string is refused. Without a valid token every route except this one answers 401.",
            rest = new
            {
                baseUrl = $"{baseUrl}/api/v1/agent",
                routes = new[]
                {
                    "GET workitems, GET workitems/{id}, GET workitems/summary",
                    "POST workitems (creates into the Backlog), PUT workitems/{id} and DELETE workitems/{id} (only items this grant created)",
                    "POST workitems/{id}/dependencies, DELETE workitems/{id}/dependencies/{dependsOnId} (only items this grant created)",
                    "POST workitems/{id}/edit-proposals, GET workitems/{id}/edit-proposals (propose an edit to any item; a human decides)",
                    "GET repositories, GET loop-templates, GET loop-runs, GET global-variables",
                    "POST global-variables/proposals, POST loop-templates/proposals, POST workitems/{id}/human-input-proposals (a human decides)",
                    "Control level only: POST workitems/{id}/start (starts a Ready item, any item), POST workitems/{id}/queue (moves an item into the work queue), POST workitems/{id}/reorder (places an item above or below another; any item, same status)",
                    "Control level only: POST workitems/{id}/human-feedback/input, /respond, /reject, /edge (answer a human-feedback stop; body {runId, input}, /edge also {name}; any item)",
                },
            },
            requestFields = new
            {
                createWorkItem = "POST workitems: title (required, 1-512 chars), description, repositoryId (required; from GET repositories), tags (a list of strings; the tags pick the loop template that runs the item), dependencies (work item ids), branchNameOverride, baseBranchOverride, loopTemplateId, createdByLoopRunId (ignored for a grant).",
                updateWorkItem = "PUT workitems/{id}: title, description, tags (replaces the list; picks the loop template), branchNameOverride, baseBranchOverride.",
                reorderWorkItem = "POST workitems/{id}/reorder: aboveId and/or belowId (at least one: the id of the work item this one goes directly after / directly before). With aboveId the item lands directly after that card in the whole board order, otherwise directly before belowId. The item keeps its status; a request that would change it is refused.",
                humanFeedback = "runId (required: the run the item is parked on), input (optional text, at most 8192 chars), name (the output name, /edge only).",
            },
            mcp = new
            {
                url = $"{baseUrl}{ILD.Api.Mcp.ExternalAgentMcp.Path}",
                transport = "Streamable HTTP, stateless",
                authentication = "The same Authorization: Bearer header.",
            },
            rules = new[]
            {
                "You are your own session: you edit and delete only the work items you created. Everything else is changed by proposing, and a human approves or rejects each proposal.",
                "You have no loop run or chat: loop variables, the current loop and chat clones are not available. Transitions other than into the work queue, linking a pull request and merging stay with the operator.",
                "There are three levels. Read only: reads, nothing else. Read, create and propose: also creates and edits work items of your own and proposes. Control of work: also starts any work item, queues it, reorders it and answers human-feedback stops, so you can drive an item from Backlog to done. A request above your level answers 403.",
                "Order: priority is the item's place in the board order and the lowest priority runs first; the scheduler starts the top-most Ready item. GET workitems?orderBy=priority returns items in that run order. Queueing keeps an item's place and a new item lands above every other one, so items queued in creation order run newest first; use reorder to set the order.",
                "A grant that can start, queue, reorder and answer acts on any work item, not only its own, and its starts, queue moves and answers appear in the item's history under this grant's name. A reorder is not a history entry; it is only audited in the grant's activity.",
                "Every request is audited under the grant, and each grant has a request budget per minute (429 when it is spent).",
            },
        });
    }

    [HttpGet("workitems")]
    public async Task<IActionResult> ListWorkItems(
        [FromQuery] string? status = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] string? createdByLoopRunId = null,
        [FromQuery] string[]? tags = null,
        [FromQuery] string? orderBy = null,
        [FromQuery] bool actionableOnly = false,
        [FromQuery] bool includeDescription = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        // Refused rather than ignored, so an old caller is not handed every item as if it had filtered them.
        if (Request.Query.ContainsKey("priority"))
            return BadRequest(new { error = "The priority filter is gone: priority is now the item's place in the board order. Use orderBy=priority to list items in board order." });
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        RemoteWorkItemStatus[]? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<RemoteWorkItemStatus>(status, true, out var s))
            statusFilter = [s];
        Guid? repoFilter = null;
        if (!string.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, out var repoGuid))
            repoFilter = repoGuid;
        Guid? runFilter = null;
        if (!string.IsNullOrEmpty(createdByLoopRunId) && Guid.TryParse(createdByLoopRunId, out var runGuid))
            runFilter = runGuid;
        var orderByValue = WorkItemOrderBy.UpdatedAt;
        if (!string.IsNullOrEmpty(orderBy) && Enum.TryParse<WorkItemOrderBy>(orderBy, true, out var ob))
            orderByValue = ob;
        var tagFilter = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        try
        {
            var items = await _workItems.ListSummariesAsync(new WorkItemListQuery
            {
                Statuses = statusFilter,
                Tags = tagFilter is { Count: > 0 } ? tagFilter : null,
                RepositoryId = repoFilter,
                CreatedByLoopRunId = runFilter,
                ActionableOnly = actionableOnly,
                OrderBy = orderByValue,
                Skip = skip,
                Take = take,
            });
            return Ok(items.Select(w => new
            {
                id = w.Id,
                title = w.Title,
                status = w.Status.ToString(),
                priority = w.Priority,
                tags = w.Tags,
                blockedBy = w.BlockedBy.Take(MaxBlockedByIds),
                blockedByCount = w.BlockedBy.Count,
                blocksCount = w.BlocksCount,
                actionable = w.IsActionable,
                repositoryId = w.RepositoryId == Guid.Empty ? null : w.RepositoryId,
                createdByLoopRunId = w.CreatedByLoopRunId,
                createdByChatSessionId = w.CreatedByChatSessionId,
                createdByExternalGrantId = w.CreatedByExternalGrantId,
                createdAt = w.CreatedAt,
                updatedAt = w.UpdatedAt,
                descriptionPreview = BuildDescriptionPreview(w.Description),
                // Full body is omitted by default (the point of the lightweight
                // list); opt back in with includeDescription for callers that
                // still want it, preserving backward compatibility.
                description = includeDescription ? w.Description : null,
            }));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    [HttpGet("workitems/summary")]
    public async Task<IActionResult> GetBacklogSummary([FromQuery] string? repositoryId = null)
    {
        Guid? repoFilter = null;
        if (!string.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, out var repoGuid))
            repoFilter = repoGuid;

        try
        {
            var summary = await _workItems.GetBacklogSummaryAsync(repoFilter);
            return Ok(new
            {
                total = summary.Total,
                countsByStatus = summary.CountsByStatus,
                blocked = summary.Blocked,
                actionable = summary.Actionable,
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    /// <summary>
    /// Collapse a work item body to a single-line preview of at most
    /// <see cref="DescriptionPreviewLength"/> characters (whitespace runs
    /// flattened to single spaces, truncated with an ellipsis). Returns null for
    /// an empty body so the list stays bodiless when there is nothing to show.
    /// </summary>
    private static string? BuildDescriptionPreview(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var singleLine = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim();
        return singleLine.Length <= DescriptionPreviewLength
            ? singleLine
            : singleLine[..DescriptionPreviewLength].TrimEnd() + "…";
    }

    [HttpGet("workitems/{id}")]
    public async Task<IActionResult> GetWorkItem(
        string id,
        [FromServices] IRunConversationService conversations,
        [FromQuery] bool includeConversation = false,
        [FromQuery] bool includeHistory = false)
    {
        if (includeConversation && await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null)
        {
            // A deleted item's history outlives it, and says who deleted it.
            if (includeHistory && await _workItems.GetHistoryAsync(id) is { } deletedHistory)
                return NotFound(new { error = $"Work item {id} has been deleted.", history = deletedHistory });
            return NotFound();
        }
        var deps = await _workItems.GetDependenciesAsync(id);
        // Reverse edges: the items that depend on this one ("blocks"). Resolved
        // here, one item at a time, so the lightweight list never pays for it.
        var blocks = await _workItems.GetDependentsAsync(id);
        var item = new
        {
            id = wi.Id,
            title = wi.Title,
            description = wi.Description,
            status = wi.Status.ToString(),
            priority = wi.Priority,
            tags = wi.Tags,
            repositoryId = wi.RepositoryId == Guid.Empty ? null : (Guid?)wi.RepositoryId,
            // The repository the current run's worktree and branch belong to,
            // which re-pointing the item for its next run does not move.
            runRepositoryId = wi.RunRepositoryId == Guid.Empty ? null : wi.RunRepositoryId,
                loopTemplateVersionId = (Guid?)null,
            createdByLoopRunId = wi.CreatedByLoopRunId,
            createdByChatSessionId = wi.CreatedByChatSessionId,
            createdByExternalGrantId = wi.CreatedByExternalGrantId,
            // An agent that may set the custom branch name has to be able to
            // read back what stuck — the API normalises it and the value it
            // holds is what the item's next run will check out.
            branchNameOverride = wi.BranchNameOverride,
            baseBranchOverride = wi.BaseBranchOverride,
            createdAt = wi.CreatedAt,
            updatedAt = wi.UpdatedAt,
            dependencies = deps.Select(d => new { id = d.Id, title = d.Title, status = d.Status.ToString() }),
            blocks = blocks.Select(b => new { id = b.Id, title = b.Title, status = b.Status.ToString() }),
            // Metadata only. The bytes come from get_workitem_attachment, one
            // file at a time, so an item with a screenshot on it stays readable.
            attachments = wi.Attachments.Select(a => new
            {
                id = a.Id,
                fileName = a.FileName,
                contentType = a.ContentType,
                sizeBytes = a.SizeBytes,
            }),
        };
        if (!includeConversation && !includeHistory) return Ok(item);

        var body = JsonSerializer.SerializeToNode(item, JsonSerializerOptions.Web)!.AsObject();
        if (includeConversation)
        {
            // The latest run's conversation, the largest field and rarely needed for
            // planning, so it is only there when asked for.
            var page = wi.LatestLoopRunId is { } runId ? await conversations.GetPageAsync(runId) : null;
            body["conversation"] = page is null
                ? null
                : JsonSerializer.SerializeToNode(new { runId = wi.LatestLoopRunId, messages = page.Messages }, JsonSerializerOptions.Web);
        }
        // How the item got to where it is, for an agent picking up or proposing an
        // edit to an item it did not create. Asked for, like the conversation.
        if (includeHistory)
            body["history"] = JsonSerializer.SerializeToNode(await _workItems.GetHistoryAsync(id), JsonSerializerOptions.Web);
        return Ok(body);
    }

    /// <summary>
    /// The bytes of one attachment an agent saw on <c>get_workitem</c>. Read-only
    /// by design: attachments are a human's, and nothing on this surface adds or
    /// removes one.
    /// </summary>
    [HttpGet("workitems/{id}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> GetWorkItemAttachment(string id, Guid attachmentId, CancellationToken cancellationToken)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        try
        {
            var attachment = await _workItems.GetAttachmentAsync(id, attachmentId, cancellationToken);
            // Served as an attachment so an uploaded page cannot run on ILD's
            // origin; nosniff comes from the security-headers middleware.
            return attachment == null
                ? NotFound()
                : File(attachment.Value.Content, attachment.Value.ContentType, attachment.Value.FileName);
        }
        catch (HttpRequestException ex)
        {
            // The same shape this surface answers every other WorkItem-server
            // outage with, so an agent can tell a missing file from an
            // unreachable one.
            return WorkItemServerUnreachable(ex);
        }
    }

    // -- Branch sync (ADR-0014) ------------------------------------------------
    //
    // The orchestrator holds the repository token and the agent can reach
    // neither it nor the askpass helper, so an agent that needs commits pushed to
    // its run branch after the run started cannot `git pull` them itself. It asks
    // here instead: the orchestrator does the authenticated fetch and the local
    // rebase, and nothing about the credential crosses the uid boundary. This is
    // not a status transition — the work item stays exactly where it is — so it
    // does not breach the human-only gate the class summary describes.

    [HttpPost("workitems/{id}/pull-branch")]
    public async Task<IActionResult> PullBranch(string id, CancellationToken cancellationToken)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        var (_, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        return PullBranchHttpResult.ToActionResult(
            await _workItems.PullBranchAsync(id, cancellationToken));
    }

    // Read-only views of a work item's worktree, uncommitted changes included. A fixed set of git commands and one
    // bounded file read, run through IWorktreeInspector (ADR-0038). Reads of a worktree are not for external-agent
    // grants: a grant's read access covers the items it created, not their code.

    [HttpGet("workitems/{id}/worktree/status")]
    public Task<IActionResult> GetWorktreeStatus(string id, [FromServices] IWorktreeInspector inspector, CancellationToken ct)
        => ReadWorktreeAsync(id, () => inspector.GetStatusAsync(id, ct));

    [HttpGet("workitems/{id}/worktree/diff")]
    public Task<IActionResult> GetWorktreeDiff(
        string id, [FromServices] IWorktreeInspector inspector, CancellationToken ct,
        [FromQuery] string? path = null, [FromQuery] bool statOnly = false)
        => ReadWorktreeAsync(id, () => inspector.GetDiffAsync(id, path, statOnly, ct));

    [HttpGet("workitems/{id}/worktree/file")]
    public Task<IActionResult> GetWorktreeFile(
        string id, [FromQuery] string path, [FromServices] IWorktreeInspector inspector, CancellationToken ct)
        => ReadWorktreeAsync(id, () => inspector.ReadFileAsync(id, path, ct));

    [HttpGet("workitems/{id}/worktree/search")]
    public Task<IActionResult> SearchWorktree(
        string id, [FromQuery] string pattern, [FromServices] IWorktreeInspector inspector, CancellationToken ct,
        [FromQuery] bool regex = false, [FromQuery] string? path = null)
        => ReadWorktreeAsync(id, () => inspector.SearchAsync(id, pattern, regex, path, ct));

    [HttpGet("workitems/{id}/worktree/log")]
    public Task<IActionResult> GetWorktreeLog(
        string id, [FromServices] IWorktreeInspector inspector, CancellationToken ct, [FromQuery] int limit = 20)
        => ReadWorktreeAsync(id, () => inspector.GetLogAsync(id, limit, ct));

    [HttpGet("workitems/{id}/worktree/commits/{sha}")]
    public Task<IActionResult> GetWorktreeCommit(string id, string sha, [FromServices] IWorktreeInspector inspector, CancellationToken ct)
        => ReadWorktreeAsync(id, () => inspector.GetCommitAsync(id, sha, ct));

    private async Task<IActionResult> ReadWorktreeAsync(string id, Func<Task<WorktreeReadResult>> read)
    {
        if (User.IsInRole(IldAuthentication.ExternalAgentRole))
            return StatusCode(403, new { error = "An external-agent grant cannot read a worktree." });
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        var result = await read();
        return result.Outcome switch
        {
            WorktreeReadOutcome.Ok => Ok(new { text = result.Text, truncated = result.Truncated, omittedBytes = result.OmittedBytes }),
            WorktreeReadOutcome.WorkItemNotFound or WorktreeReadOutcome.NotFound => NotFound(new { error = result.Message }),
            WorktreeReadOutcome.NoWorktree => Conflict(new { error = result.Message }),
            WorktreeReadOutcome.Invalid => BadRequest(new { error = result.Message }),
            _ => StatusCode(500, new { error = result.Message }),
        };
    }

    /// <summary>
    /// Clone a repository into the calling scheduled chat's <c>repos/</c> folder,
    /// or update the clone made there before, if the schedule allows it. The git
    /// work outlives a cancelled request, so a caller that timed out calls again
    /// and finds it done.
    /// </summary>
    [HttpPost("chat/repositories/{repositoryId}/clone")]
    public async Task<IActionResult> CloneRepositoryIntoChat(
        string repositoryId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AgentChatRepositoryCloneRequest? request,
        [FromServices] IChatRepositoryCloner cloner,
        CancellationToken cancellationToken)
    {
        if (Caller.ChatSessionId is not { } chatSessionId)
            return BadRequest(new { error = "A chat-session id is required. Only an agent launched for a chat has one." });

        return ChatRepositoryCloneHttpResult.ToActionResult(
            await cloner.CloneAsync(chatSessionId, repositoryId, request?.Branch, cancellationToken));
    }

    /// <summary>
    /// Fills <see cref="WorktreePreviewServiceResponse.AgentUrl"/> with the address
    /// that works where the caller runs. A run on this work item shares the preview's
    /// network namespace, so it gets the loopback address (a browser there resolves
    /// <c>*.localhost</c> to itself, not to ILD); a chat, or a run on another work item, gets the
    /// agents' hostname (<see cref="AgentPreviewUrl"/>). Works on a copy: the service hands out a shared snapshot.
    /// </summary>
    private async Task<WorktreePreviewResponse> ForCallerAsync(string workItemId, WorktreePreviewResponse response)
    {
        var runId = Caller.RunId;
        var ownRun = runId is Guid id && await _sessions.RunWorksOnAsync(id, workItemId);

        var proxyBase = HttpContext?.RequestServices?.GetService<PreviewProxyBase>() ?? PreviewProxyBase.Disabled;
        var egress = HttpContext?.RequestServices?.GetService<EgressProxyOptions>() ?? EgressProxyOptions.Disabled;
        var copy = response.Clone();
        foreach (var service in copy.Services)
            service.AgentUrl = ownRun ? service.LocalUrl ?? service.PublicUrl : AgentPreviewUrl(service.PublicUrl, proxyBase, egress);
        return copy;
    }

    private async Task<IActionResult> ForCallerAsync(string workItemId, Task<PreviewResult<WorktreePreviewResponse>> preview)
        => (await (await preview).MapAsync(response => ForCallerAsync(workItemId, response))).ToActionResult();

    /// <summary>
    /// The address an agent outside the work item's run opens <paramref name="publicUrl"/> at. A preview the proxy
    /// serves is given by its agents' hostname (<c>http://wi-12.preview.ild/</c>), which the agent's egress proxy hands
    /// to ILD: the public one is often <c>*.localhost</c>, which in a chat's or run's container is that container. Any
    /// other public URL, and any when there is no egress proxy to carry the agents' hostname, is given as it is.
    /// </summary>
    internal static string? AgentPreviewUrl(string? publicUrl, PreviewProxyBase proxyBase, EgressProxyOptions egress)
        => egress.Enabled && Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) && proxyBase.TryGetHostLabel(uri.Host, out var label)
            ? PreviewProxyBase.BuildAgentUrl(label)
            : publicUrl;

    // -- Worktree preview controls (ADR-0011) ----------------------------------
    //
    // These mirror the human WorkItemsController preview surface so the chat agent
    // can drive a work item's preview. Each takes an explicit work item id (read
    // from the Chat Context). They fold under the `ild` grant. Like the human
    // controller, every mutating endpoint broadcasts PreviewStateChanged so an open
    // Preview tab live-updates when an agent starts or stops the preview.

    [HttpGet("workitems/{id}/preview")]
    public async Task<IActionResult> GetPreview(string id)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return await ForCallerAsync(id, _preview.GetStatusAsync(id));
    }

    [HttpPost("workitems/{id}/preview/start")]
    public async Task<IActionResult> StartPreview(string id, [FromBody] WorktreePreviewStartRequest? request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return await ForCallerAsync(id, _preview.StartAsync(id, request));
    }

    [HttpPost("workitems/{id}/preview/stop")]
    public async Task<IActionResult> StopPreview(string id)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return await ForCallerAsync(id, _preview.StopAsync(id));
    }

    [HttpPost("workitems/{id}/preview/services/{service}/start")]
    public async Task<IActionResult> StartPreviewService(string id, string service, [FromBody] WorktreePreviewStartRequest? request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return await ForCallerAsync(id, _preview.StartServiceAsync(id, service, request, startDependencies: false));
    }

    [HttpPost("workitems/{id}/preview/services/{service}/stop")]
    public async Task<IActionResult> StopPreviewService(string id, string service)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return await ForCallerAsync(id, _preview.StopServiceAsync(id, service));
    }

    [HttpGet("workitems/{id}/preview/services/{service}/config")]
    public async Task<IActionResult> GetPreviewServiceConfig(string id, string service)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return (await _preview.GetServiceConfigAsync(id, service)).ToActionResult();
    }

    [HttpPut("workitems/{id}/preview/services/{service}/config")]
    public async Task<IActionResult> UpdatePreviewServiceConfig(string id, string service, [FromBody] WorktreePreviewServiceConfigUpdateRequest? request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return (await _preview.UpdateServiceConfigAsync(id, service, request)).ToActionResult();
    }

    [HttpGet("workitems/{id}/preview/logs")]
    public async Task<IActionResult> GetPreviewLog(string id, [FromQuery] string service)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        return (await _preview.GetLogAsync(id, service)).ToActionResult();
    }

    /// <summary>
    /// The tail of one failing CI check's log for the work item's current run.
    /// The check id comes from the failure reason the PR node handed the agent;
    /// the fetch itself runs here, with the provider credentials the agent does
    /// not hold. A provider with no fetchable log answers 200 with
    /// <c>available:false</c> and a message — it is an answer, not an error.
    /// </summary>
    [HttpGet("workitems/{id}/ci-log")]
    public async Task<IActionResult> GetCiLog(
        string id,
        [FromServices] IPrCiLogService ciLogs,
        [FromQuery] string checkId,
        [FromQuery] int tailLines = PrCiLogService.DefaultTailLines,
        [FromQuery] int offset = 0)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        if (string.IsNullOrWhiteSpace(checkId))
            return BadRequest(new { error = "checkId is required." });

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var log = await ciLogs.ReadAsync(id, checkId, tailLines, offset);
        return Ok(new
        {
            checkId,
            available = log.Available,
            text = log.Text,
            lines = log.Lines,
            offset = log.Offset,
            totalLines = log.TotalLines,
            truncated = log.Truncated,
            message = log.Message,
        });
    }

    /// <summary>
    /// The whole review ledger for the work item's current pull request: every
    /// inline comment with its thread and the commit it was written against,
    /// every finding a review body suppressed without surfacing it as a thread,
    /// and each review's verdict. <c>sinceCommit</c> narrows it to what a round
    /// has not already seen. The fetch runs here, with the provider credentials
    /// the agent does not hold.
    /// </summary>
    [HttpGet("workitems/{id}/pr-review")]
    public async Task<IActionResult> GetPrReview(
        string id,
        [FromServices] IPrReviewService reviews,
        [FromQuery] string? sinceCommit = null)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var ledger = await reviews.ReadAsync(id, sinceCommit, Caller.RunId);
        return Ok(new
        {
            headSha = ledger.HeadSha,
            message = ledger.Message,
            reviews = ledger.Reviews.Select(r => new
            {
                id = r.Id,
                state = r.State,
                // The review own prose. Also delivered as a "body" item, but an
                // agent reading the ledger directly must be able to see it here.
                body = r.Body,
                headSha = r.HeadSha,
                submittedAt = r.SubmittedAt,
                author = r.Author,
                incomplete = r.Incomplete,
            }),
            items = ledger.Items.Select(i => new
            {
                kind = i.Kind,
                commentId = i.CommentId,
                threadId = i.ThreadId,
                reviewId = i.ReviewId,
                path = i.Path,
                line = i.Line,
                body = i.Body,
                author = i.Author,
                commit = i.Commit,
                createdAt = i.CreatedAt,
                resolved = i.Resolved,
                postedByIld = i.PostedByIld,
            }),
        });
    }

    /// <summary>
    /// Answer one review comment on its own thread, so an item that was argued
    /// rather than changed stops coming back on every later review. A refusal —
    /// a comment id this pull request does not hold, a provider that would not
    /// take it — is 200 with a message, as <see cref="GetCiLog"/> is.
    /// </summary>
    [HttpPost("workitems/{id}/pr-review/reply")]
    public async Task<IActionResult> ReplyToPrReview(
        string id,
        [FromServices] IPrReviewService reviews,
        [FromBody] AgentPrReviewReplyRequest request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        if (string.IsNullOrWhiteSpace(request?.CommentId) || string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new { error = "commentId and body are required." });

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var result = await reviews.ReplyAsync(id, request.CommentId, request.Body, Caller.RunId, Caller.ChatSessionId);
        return Ok(new { ok = result.Ok, commentId = result.Id, writeId = result.Id, message = result.Message });
    }

    /// <summary>Mark a review thread resolved once it has been answered or fixed.</summary>
    [HttpPost("workitems/{id}/pr-review/resolve")]
    public async Task<IActionResult> ResolvePrReviewThread(
        string id,
        [FromServices] IPrReviewService reviews,
        [FromBody] AgentPrReviewResolveRequest request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        if (string.IsNullOrWhiteSpace(request?.ThreadId))
            return BadRequest(new { error = "threadId is required." });

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var result = await reviews.ResolveAsync(id, request.ThreadId, Caller.RunId, Caller.ChatSessionId);
        return Ok(new { ok = result.Ok, threadId = request.ThreadId, writeId = result.Id, message = result.Message });
    }

    /// <summary>
    /// Say something general on the pull request, tied to no item. This is the
    /// round's own account of itself, and it exists because the PR node no
    /// longer writes one: only the round knows whether it has anything to add
    /// beyond the answers it gave on the threads, so a round with nothing to add
    /// leaves the pull request quiet.
    /// </summary>
    [HttpPost("workitems/{id}/pr-review/comment")]
    public async Task<IActionResult> CommentOnPr(
        string id,
        [FromServices] IPrReviewService reviews,
        [FromBody] AgentPrCommentRequest request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        if (string.IsNullOrWhiteSpace(request?.Body))
            return BadRequest(new { error = "body is required." });

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var result = await reviews.CommentAsync(id, request.Body, Caller.RunId, Caller.ChatSessionId);
        return Ok(new { ok = result.Ok, writeId = result.Id, message = result.Message });
    }

    /// <summary>
    /// Record that this round read an item and chose not to answer it, closing
    /// its thread as well when <c>resolve</c> is set and the forge can. Closing
    /// suppresses nothing that was not already suppressed: the item was recorded
    /// as delivered when it was handed over, and that record is per-head, so a
    /// reviewer restating the point against new code still fires.
    /// </summary>
    [HttpPost("workitems/{id}/pr-review/close")]
    public async Task<IActionResult> ClosePrReviewItem(
        string id,
        [FromServices] IPrReviewService reviews,
        [FromBody] AgentPrReviewCloseRequest request)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        if (string.IsNullOrWhiteSpace(request?.CommentId))
            return BadRequest(new { error = "commentId is required." });

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var result = await reviews.CloseAsync(id, request.CommentId, request.Resolve, Caller.RunId, Caller.ChatSessionId);
        return Ok(new { ok = result.Ok, commentId = request.CommentId, writeId = result.Id, message = result.Message });
    }

    /// <summary>
    /// The writes the caller has waiting for the PR node on the work item's
    /// current pull request — its own only. Read from the queue alone, so it
    /// answers even when the forge cannot be reached.
    /// </summary>
    [HttpGet("workitems/{id}/pr-review/queue")]
    public async Task<IActionResult> ListQueuedPrWrites(string id, [FromServices] IPrReviewService reviews)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var view = await reviews.ListQueuedAsync(id, Caller.RunId, Caller.ChatSessionId);
        return Ok(new
        {
            message = view.Message,
            writes = view.Writes.Select(w => new
            {
                id = w.Id,
                kind = w.Kind,
                targetId = w.TargetId,
                itemId = w.ItemId,
                body = w.Body,
                path = w.Path,
                line = w.Line,
                queuedAt = w.QueuedAt,
            }),
        });
    }

    /// <summary>
    /// Take back a write the caller queued, before the PR node sends it. A
    /// write that is not the caller's, or no longer waiting, is a refusal —
    /// 200 with a message, as a refused reply is.
    /// </summary>
    [HttpDelete("workitems/{id}/pr-review/queue/{writeId}")]
    public async Task<IActionResult> WithdrawQueuedPrWrite(
        string id, string writeId, [FromServices] IPrReviewService reviews)
    {
        if (await CallerMayTouchAsync(id) is { } forbidden)
            return forbidden;

        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        var result = await reviews.WithdrawAsync(id, writeId, Caller.RunId, Caller.ChatSessionId);
        return Ok(new { ok = result.Ok, writeId, message = result.Message });
    }

    [HttpGet("repositories")]
    public async Task<IActionResult> ListRepositories([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var repos = await _db.Repositories.AsNoTracking()
            .OrderBy(r => r.Name)
            .Skip(skip).Take(take)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                cloneUrl = r.CloneUrl,
                defaultBranch = r.DefaultBranch,
                defaultIntakeStatus = r.DefaultIntakeStatus.ToString(),
            })
            .ToListAsync();
        // Stripped after the query: a clone URL may carry credentials an agent must not read.
        return Ok(repos.Select(r => new { r.id, r.name, cloneUrl = RepositoryManager.WithoutUserInfo(r.cloneUrl), r.defaultBranch, r.defaultIntakeStatus }));
    }

    [HttpGet("loop-templates")]
    public async Task<IActionResult> ListLoopTemplates([FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] bool includeArchived = false)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var templates = await _templates.GetAllLoopTemplatesAsync(skip, take, includeArchived);
        return Ok(templates.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            description = t.Description ?? string.Empty,
            isDefault = t.IsDefault,
            isArchived = t.IsArchived,
        }));
    }

    /// <summary>
    /// A saved loop template version as an <c>ild-loop-template/v2</c> document, the
    /// format <c>current-loop</c> reads and takes. <paramref name="nameOrId"/> is a
    /// template id, or else an exact name (case and surrounding whitespace ignored,
    /// archived templates included); a name more than one template has is 409 with
    /// the candidates. No <paramref name="version"/> means the latest.
    /// </summary>
    [HttpGet("loop-templates/document")]
    public async Task<IActionResult> GetLoopTemplateDocument(
        [FromServices] ILoopTemplateLookup lookup, [FromQuery] string? nameOrId = null, [FromQuery] int? version = null)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
            return BadRequest(new { error = "nameOrId is required: a loop template id or name." });
        if (version < 1)
            return BadRequest(new { error = "version must be 1 or greater." });

        // One snapshot: a save writes the template's name and policy with its new version, so separate reads could mix them.
        await using var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var (template, notFound) = await FindLoopTemplateAsync(lookup, nameOrId);
        if (template == null)
            return notFound!;

        var versions = (await _templates.GetVersionsAsync(template.Id)).ToList();
        var latest = versions.MaxBy(v => v.VersionNumber);
        if (latest == null)
            return NotFound(new { error = $"Loop template {template.Id} has no saved version." });
        var chosen = version is int number ? versions.SingleOrDefault(v => v.VersionNumber == number) : latest;
        if (chosen == null)
            return NotFound(new
            {
                error = $"Loop template {template.Id} has no version {version}; the latest is {latest.VersionNumber}.",
                latestVersion = latest.VersionNumber,
            });

        var graph = await _templates.GetVersionGraphAsync(template.Id, chosen.VersionNumber);
        if (graph == null)
            return NotFound(new { error = $"Loop template {template.Id} has no version {chosen.VersionNumber}." });
        await snapshot.CommitAsync();
        return Ok(new
        {
            templateId = template.Id,
            version = chosen.VersionNumber,
            latestVersion = latest.VersionNumber,
            isLatest = chosen.VersionNumber == latest.VersionNumber,
            versionCreatedAt = chosen.CreatedAt,
            isArchived = template.IsArchived,
            document = new RawJson(LoopTemplateDocument.From(template, graph).Render()),
        });
    }

    private async Task<(LoopTemplate? Template, IActionResult? Error)> FindLoopTemplateAsync(ILoopTemplateLookup lookup, string nameOrId)
    {
        var found = await lookup.FindAsync(nameOrId);
        if (found.Template is { } template)
            return (template, null);
        return found.Outcome == LoopTemplateLookupOutcome.Ambiguous
            ? (null, AmbiguousLoopTemplateName(found.Error!, found.Candidates!))
            : (null, NotFound(new { error = found.Error }));
    }

    private ConflictObjectResult AmbiguousLoopTemplateName(string error, IReadOnlyList<LoopTemplate> candidates)
        => Conflict(new
        {
            error,
            candidates = candidates.OrderBy(t => t.IsArchived).ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new { id = t.Id, name = t.Name, isArchived = t.IsArchived }),
        });

    /// <summary>
    /// The analytics dashboard's per-template rows for the same filters, each with
    /// per-node figures from the runs that still exist (<c>runsWithNodeDetail</c>
    /// of them; deleted runs keep no per-node history). A finished run is kept for
    /// the run retention period and then deleted; its worktree goes when it ends,
    /// unless it is pinned.
    /// </summary>
    [HttpGet("analytics/templates")]
    public async Task<IActionResult> GetTemplateAnalytics(
        [FromServices] IRunAnalyticsService analytics,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? templateId = null,
        CancellationToken cancellationToken = default)
    {
        if (ParseRunFilters(from, to, templateId, out var fromDate, out var toDate, out var templateGuid) is { } invalid)
            return invalid;

        var query = new AnalyticsQuery(fromDate, toDate, string.IsNullOrWhiteSpace(provider) ? null : provider);
        var rows = await analytics.GetTemplateAnalyticsAsync(query, templateGuid, cancellationToken);
        return Ok(new
        {
            from = query.From,
            to = query.To,
            provider = query.Provider,
            templateId = templateGuid,
            templates = rows.Select(r => new
            {
                templateId = r.Totals.TemplateId,
                templateName = r.Totals.TemplateName,
                totalRuns = r.Totals.TotalRuns,
                completedRuns = r.Totals.CompletedRuns,
                failedRuns = r.Totals.FailedRuns,
                cancelledRuns = r.Totals.CancelledRuns,
                successRate = r.Totals.SuccessRate,
                avgNodeSeconds = r.Totals.AvgNodeSeconds,
                onFailureRoutings = r.Totals.OnFailureRoutings,
                rejectRoutings = r.Totals.RejectRoutings,
                avgFeedbackSeconds = r.Totals.AvgHumanFeedbackSeconds,
                totalInputTokens = r.Totals.TotalInputTokens,
                totalOutputTokens = r.Totals.TotalOutputTokens,
                totalCostUsd = r.Totals.TotalCostUsd,
                runsWithNodeDetail = r.RunsWithNodeDetail,
                nodes = r.Nodes,
            }),
        });
    }

    /// <summary>
    /// Loop runs, newest first by start (creation for a run not yet started).
    /// <paramref name="from"/>/<paramref name="to"/> are inclusive UTC days on that
    /// same start, as on the analytics dashboard.
    /// </summary>
    [HttpGet("loop-runs")]
    public async Task<IActionResult> ListLoopRuns(
        [FromQuery] string? workItemId = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? templateId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        if (ParseRunFilters(from, to, templateId, out var fromDate, out var toDate, out var templateGuid) is { } invalid)
            return invalid;

        var query = _db.LoopRuns.AsNoTracking();
        if (!string.IsNullOrEmpty(workItemId))
            query = query.Where(r => r.WorkItemId == workItemId);
        if (templateGuid is Guid template)
            query = query.Where(r => r.LoopTemplateVersion.LoopTemplateId == template);
        if (fromDate is DateOnly fromDay)
        {
            var start = fromDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(r => (r.StartedAt ?? r.CreatedAt) >= start);
        }
        if (toDate is DateOnly toDay && toDay < DateOnly.MaxValue)
        {
            var end = toDay.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(r => (r.StartedAt ?? r.CreatedAt) < end);
        }

        // One snapshot, so each row's cost totals come from the same moment as the row.
        await using var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var runs = await query
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip).Take(take)
            .Select(r => new
            {
                r.Id,
                r.WorkItemId,
                r.Status,
                r.StartedAt,
                r.CompletedAt,
                TemplateId = r.LoopTemplateVersion.LoopTemplateId,
                TemplateName = r.LoopTemplateVersion.LoopTemplate.Name,
                TemplateVersion = r.LoopTemplateVersion.VersionNumber,
            })
            .ToListAsync();

        var costs = await AggregateRunCostsAsync(runs.Select(r => r.Id).ToList());
        await snapshot.CommitAsync();
        return Ok(runs.Select(r =>
        {
            costs.TryGetValue(r.Id, out var cost);
            return new
            {
                id = r.Id,
                workItemId = r.WorkItemId,
                status = r.Status.ToString(),
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                costUsd = cost?.CostUsd,
                inputTokens = cost?.InputTokens ?? 0,
                outputTokens = cost?.OutputTokens ?? 0,
                templateId = r.TemplateId,
                templateName = r.TemplateName,
                templateVersion = r.TemplateVersion,
            };
        }));
    }

    /// <summary>
    /// One run's path through its template, its human feedback, its counters and its
    /// pull request's last stored review state, as a bounded summary. Read from what
    /// is stored; nothing is fetched from the forge.
    /// </summary>
    [HttpGet("loop-runs/{id}")]
    public async Task<IActionResult> GetLoopRun(
        string id, [FromServices] ILoopRunDigestService digests, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var runId))
            return BadRequest(new { error = $"Invalid run id '{id}'; expected a GUID." });

        var digest = await digests.GetAsync(runId, cancellationToken);
        return digest == null ? NotFound(new { error = $"Loop run not found: {runId}" }) : Ok(digest);
    }

    /// <summary>The run filters shared by the run list and template analytics, or the 400 naming the malformed one.</summary>
    private IActionResult? ParseRunFilters(
        string? from, string? to, string? templateId,
        out DateOnly? fromDate, out DateOnly? toDate, out Guid? templateGuid)
    {
        toDate = null;
        templateGuid = null;
        if (!AnalyticsController.TryParseDate(from, out fromDate))
            return BadRequest(new { error = "Invalid 'from' date; expected yyyy-MM-dd." });
        if (!AnalyticsController.TryParseDate(to, out toDate))
            return BadRequest(new { error = "Invalid 'to' date; expected yyyy-MM-dd." });
        if (string.IsNullOrWhiteSpace(templateId))
            return null;
        if (!Guid.TryParse(templateId, out var parsed))
            return BadRequest(new { error = "Invalid 'templateId'; expected a loop template GUID." });
        templateGuid = parsed;
        return null;
    }

    private sealed record RunCost(decimal? CostUsd, long InputTokens, long OutputTokens);

    /// <summary>
    /// Roll up per-run token/cost totals from <c>LoopRunNode</c> rows for the
    /// given run ids in one query. Backs the read-only run cost visibility the
    /// chat agent gets (ADR-0011). Cost is null for a run whose nodes reported
    /// no monetary figure (e.g. subscription-auth providers).
    /// </summary>
    private async Task<Dictionary<Guid, RunCost>> AggregateRunCostsAsync(IReadOnlyList<Guid> runIds)
    {
        if (runIds.Count == 0) return new Dictionary<Guid, RunCost>();

        var rows = await _db.LoopRunNodes.AsNoTracking()
            .Where(n => runIds.Contains(n.LoopRunId))
            .GroupBy(n => n.LoopRunId)
            .Select(g => new
            {
                RunId = g.Key,
                CostUsd = g.Sum(n => n.CostUsd),
                InputTokens = g.Sum(n => n.InputTokens ?? 0),
                OutputTokens = g.Sum(n => n.OutputTokens ?? 0),
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.RunId, r => new RunCost(r.CostUsd, r.InputTokens, r.OutputTokens));
    }

    [HttpGet("variables")]
    public async Task<IActionResult> ListVariables()
    {
        if (Caller.RunId is not { } runId)
            return BadRequest(new { error = "A loop-run id is required. Only an agent launched for a run has one." });

        var variables = await _runs.GetVariablesAsync(runId);
        return Ok(variables.Select(v => new
        {
            name = v.Name,
            value = v.Value,
            updatedAt = v.UpdatedAt ?? v.CreatedAt,
        }));
    }

    [HttpPut("variables/{name}")]
    public async Task<IActionResult> SetVariable(string name, [FromBody] AgentSetVariableRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (Caller.RunId is not { } runId)
            return BadRequest(new { error = "A loop-run id is required. Only an agent launched for a run has one." });

        if (!PromptPlaceholderRegistry.IsValidVariableName(name))
            return BadRequest(new { error = "Variable name must start with a letter and contain only letters, digits, and underscores." });

        if (!await _sessions.RunExistsAsync(runId))
            return NotFound(new { error = $"Loop run not found: {runId}" });

        await _runs.SetVariableAsync(runId, name, request.Value ?? string.Empty);
        return Ok(new { name, value = request.Value ?? string.Empty });
    }

    // -- Control of work (ADR-0037, amended by ADR-0039) -------------------------
    //
    // A grant at the Control level drives the work it is handed: it starts any
    // work item, moves one into the work queue, reorders one within its status, and answers a human-feedback
    // stop. Run and chat agents and the other grant levels are refused. Nothing
    // else moves: other transitions, link-pr and merge stay with the operator.

    private async Task<(WorkItemActor? Actor, IActionResult? Error)> ControlActorAsync()
    {
        if (!Caller.HasControlGrant)
            return (null, StatusCode(403, new { error = "Only an external-agent grant with control of work can start work items, queue them, reorder them or answer human feedback." }));
        return await _sessions.ActorAsync(Caller) is { Kind: RemoteWorkItemActorKind.ExternalAgent } actor
            ? (actor, null)
            : (null, StatusCode(403, new { error = "This grant no longer exists." }));
    }

    /// <summary>Start a Ready item now, as <c>POST /api/v1/workitems/{id}/start</c> does for the operator.</summary>
    [HttpPost("workitems/{id}/start")]
    public async Task<IActionResult> StartWorkItem(
        string id,
        [FromServices] IRemoteWorkItemCoordinator coordinator,
        [FromServices] ISchedulerSettingsService schedulerSettings,
        [FromServices] IWorkItemServerOptionsResolver serverOptions,
        [FromServices] IWorkItemScheduler scheduler,
        CancellationToken cancellationToken)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;

        try
        {
            var opts = await serverOptions.ResolveForWorkItemAsync(id, cancellationToken);
            var maxConcurrent = await schedulerSettings.GetMaxConcurrentAsync(cancellationToken);
            switch (await coordinator.StartIgnoringRepositoryLimitAsync(opts, id, maxConcurrent, actor, cancellationToken))
            {
                case HandStartResult.NotFound:
                    return NotFound();
                case HandStartResult.NotReady:
                    return Conflict(new { error = "Only a Ready item can be started." });
                case HandStartResult.AtConcurrencyLimit:
                    return Conflict(new { error = "ILD is already running as many work items as it is allowed to." });
                case HandStartResult.ShuttingDown:
                    return Conflict(new { error = "ILD is shutting down." });
            }
            scheduler.Pulse();
            return await _workItems.GetWorkItemAsync(id) is { } started ? Ok(started) : NotFound();
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    /// <summary>Move an item into the work queue, where the scheduler picks it up. No other status can be reached from here.</summary>
    [HttpPost("workitems/{id}/queue")]
    public async Task<IActionResult> QueueWorkItem(string id)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;

        try
        {
            return await _workItems.MoveAsync(actor, id, RemoteWorkItemStatus.WorkQueue)
                ? Ok()
                : BadRequest(new { error = "Transition not allowed" });
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    /// <summary>
    /// Place an item above or below another, as the operator's drop on the board does. Within the item's current status only:
    /// the route has no target status, so it never transitions.
    /// </summary>
    [HttpPost("workitems/{id}/reorder")]
    public async Task<IActionResult> ReorderWorkItem(string id, [FromBody] AgentWorkItemReorderRequest request)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;

        try
        {
            return await _workItems.ReorderAsync(actor, id, request.AboveId, request.BelowId) switch
            {
                ReorderOutcome.NotFound => NotFound(),
                ReorderOutcome.InvalidNeighbour => BadRequest(new { error = "aboveId or belowId must name another work item." }),
                ReorderOutcome.TransitionRefused => BadRequest(new { error = "Transition not allowed" }),
                _ => await _workItems.GetWorkItemAsync(id) is { } moved ? Ok(moved) : NotFound(),
            };
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    [HttpPost("workitems/{id}/human-feedback/input")]
    public async Task<IActionResult> HumanFeedbackInput(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        return await AnswerAsync(request.RunId,
            runId => _workItems.SubmitHumanFeedbackInputAsync(id, runId, request.Input ?? string.Empty, actor));
    }

    [HttpPost("workitems/{id}/human-feedback/respond")]
    public async Task<IActionResult> HumanFeedbackRespond(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        return await AnswerAsync(request.RunId,
            runId => _workItems.SubmitHumanFeedbackRespondAsync(id, runId, request.Input ?? string.Empty, actor));
    }

    [HttpPost("workitems/{id}/human-feedback/reject")]
    public async Task<IActionResult> HumanFeedbackReject(string id, [FromBody] HumanFeedbackRejectRequest request)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;
        if (request.Input is { Length: > 8192 })
            return BadRequest(new { error = "Input exceeds 8192 characters" });

        return await AnswerAsync(request.RunId,
            runId => _workItems.RejectHumanFeedbackAsync(id, runId, request.Input, actor));
    }

    [HttpPost("workitems/{id}/human-feedback/edge")]
    public async Task<IActionResult> HumanFeedbackEdge(string id, [FromBody] HumanFeedbackEdgeRequest request)
    {
        var (actor, error) = await ControlActorAsync();
        if (actor is null)
            return error!;
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Edge name is required" });

        return await AnswerAsync(request.RunId,
            runId => _workItems.SubmitHumanFeedbackEdgeAsync(id, runId, request.Name, request.Input ?? string.Empty, actor));
    }

    /// <summary>Deliver an answer to the run it names, as the operator's route does; a stale or closed run is a 409.</summary>
    private async Task<IActionResult> AnswerAsync(Guid? runId, Func<Guid, Task<bool>> deliver)
    {
        if (runId is not { } run || run == Guid.Empty)
            return BadRequest(new { error = "The answer must name the run it answers (runId)." });

        bool ok;
        try { ok = await deliver(run); }
        catch (RunClosedException ex) { return Conflict(new { error = ex.Message }); }
        catch (HumanFeedbackRefusedException ex) { return Conflict(new { error = ex.Message }); }
        return ok ? Ok() : NotFound();
    }

    // -- Loop Editor context (ADR-0011) ----------------------------------------
    //
    // The browser stashes the loop currently open in the Loop Editor into a
    // per-session scratchpad on every chat message; the agent reads it here and
    // pushes a full-document replacement back to the open editor. Both are scoped
    // to the chat session its token names. The agent is never
    // given a persist tool — update_current_loop mutates transient client state
    // only; the sole write to a LoopTemplateVersion stays the editor's human Save.

    // The authoring guide is static text, so it is served without a chat session:
    // it describes the loop template model itself, not the loop anyone has open. The
    // chat pushes it once per agent session and this endpoint is how the agent gets
    // it back for the rest of that session (#27).
    [HttpGet("loop-authoring-guide")]
    public IActionResult GetLoopAuthoringGuide()
        => Content(LoopAuthoringGuide.Text, "text/plain");

    [HttpGet("current-loop")]
    public async Task<IActionResult> GetCurrentLoop()
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;

        var document = _loopScratchpad.Get(chatSessionId);
        // No document stashed this turn means the Loop Editor is not open. Report it
        // as data (not an error) so the tool surfaces a clean "no loop open" notice.
        if (string.IsNullOrWhiteSpace(document))
            return Ok(new { loopEditorOpen = false });

        return Content(document, "application/json");
    }

    [HttpPut("current-loop")]
    public async Task<IActionResult> UpdateCurrentLoop([FromBody] AgentLoopUpdateRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrWhiteSpace(request.Document))
            return BadRequest(new { error = "document is required — pass a complete ild-loop-template/v2 document." });
        if (request.Document.Length > LoopTemplateProposalService.MaxLoopDocumentChars)
            return BadRequest(new { error = $"document is too large ({request.Document.Length} chars); the limit is {LoopTemplateProposalService.MaxLoopDocumentChars}." });

        // Retrofit (ADR-0011): the server now validates the full replacement itself
        // (reject-on-invalid) and returns the same synchronous ack as the scoped
        // edits. A valid document is stashed and pushed to the canvas; a rejected one
        // leaves both untouched and the agent is told now, not next turn.
        var result = LoopDocumentEditor.ReplaceDocument(request.Document);
        return await ApplyEditAsync(chatSessionId, result);
    }

    [HttpGet("current-loop/file")]
    public async Task<IActionResult> GetLoopFile()
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;

        var document = _loopScratchpad.Get(chatSessionId);
        if (string.IsNullOrWhiteSpace(document))
            return Ok(new { loopEditorOpen = false });

        return Content(document, "application/json");
    }

    [HttpPost("current-loop/file/edit")]
    public async Task<IActionResult> EditLoopFile([FromBody] AgentLoopFileEditRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrEmpty(request.OldString))
            return BadRequest(new { error = "old_string is required." });

        var notOpen = RequireOpenLoop(chatSessionId, out var document);
        if (notOpen != null) return notOpen;

        var result = LoopDocumentEditor.EditFile(document, request.OldString, request.NewString ?? string.Empty);
        return await ApplyEditAsync(chatSessionId, result);
    }

    [HttpGet("current-loop/nodes/{nodeId}")]
    public async Task<IActionResult> GetLoopNode(string nodeId)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;

        var document = _loopScratchpad.Get(chatSessionId);
        if (string.IsNullOrWhiteSpace(document))
            return Ok(new { loopEditorOpen = false });

        var (found, nodeJson, nodeError) = LoopDocumentEditor.GetNode(document, nodeId);
        if (!found)
            return NotFound(new { error = nodeError });
        return Content(nodeJson!, "application/json");
    }

    [HttpPost("current-loop/nodes/{nodeId}/edit-field")]
    public async Task<IActionResult> EditLoopNodeField(string nodeId, [FromBody] AgentLoopNodeFieldEditRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrWhiteSpace(request.Field))
            return BadRequest(new { error = LoopDocumentEditor.FieldRequired });

        var notOpen = RequireOpenLoop(chatSessionId, out var document);
        if (notOpen != null) return notOpen;

        var result = LoopDocumentEditor.EditNodeField(
            document, nodeId, request.Field, request.OldString ?? string.Empty, request.NewString ?? string.Empty);
        return await ApplyEditAsync(chatSessionId, result);
    }

    [HttpPost("current-loop/nodes/{nodeId}/set-field")]
    public async Task<IActionResult> SetLoopNodeField(string nodeId, [FromBody] AgentLoopNodeFieldSetRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrWhiteSpace(request.Field))
            return BadRequest(new { error = LoopDocumentEditor.FieldRequired });

        var notOpen = RequireOpenLoop(chatSessionId, out var document);
        if (notOpen != null) return notOpen;

        var result = LoopDocumentEditor.SetNodeField(document, nodeId, request.Field, request.Value ?? string.Empty);
        return await ApplyEditAsync(chatSessionId, result);
    }

    /// <summary>
    /// The scoped edits act on the document the browser stashed this turn. When no
    /// loop is open there is nothing to edit — reported as a clean <c>applied:false</c>
    /// ack (not an HTTP error) so the agent gets the same structured shape it gets for
    /// a bad match or a validation failure. Returns null (and the document) when a
    /// loop is open.
    /// </summary>
    private IActionResult? RequireOpenLoop(Guid chatSessionId, out string document)
    {
        document = _loopScratchpad.Get(chatSessionId) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(document))
            return null;

        return Ok(new
        {
            applied = false,
            matchCount = 0,
            validationErrors = Array.Empty<string>(),
            error = "No loop is open in the Loop Editor, so there is nothing to edit.",
        });
    }

    /// <summary>
    /// Turn a <see cref="LoopEditResult"/> into the synchronous ack every loop edit
    /// surface returns, and — only when the edit applied — stash the new document in
    /// the scratchpad and push it to the open canvas via the same
    /// <c>LoopUpdateRequested</c> path a full replacement uses. A rejected edit (bad
    /// match or failed validation) leaves both the scratchpad and the canvas exactly
    /// as they were, but the agent is told now rather than next turn (ADR-0011).
    /// </summary>
    private async Task<IActionResult> ApplyEditAsync(Guid chatSessionId, LoopEditResult result)
    {
        if (result is { Applied: true, Document: not null })
        {
            if (result.Document.Length > LoopTemplateProposalService.MaxLoopDocumentChars)
                return Ok(new
                {
                    applied = false,
                    matchCount = result.MatchCount,
                    validationErrors = Array.Empty<string>(),
                    error = $"The edited document is too large ({result.Document.Length} chars); the limit is {LoopTemplateProposalService.MaxLoopDocumentChars}.",
                    summary = (string?)null,
                });

            _loopScratchpad.Set(chatSessionId, result.Document);
            await _chatNotifier.LoopUpdateRequestedAsync(chatSessionId, result.Document);
        }

        return Ok(new
        {
            applied = result.Applied,
            matchCount = result.MatchCount,
            validationErrors = result.ValidationErrors,
            error = result.Error,
            summary = result.Summary,
        });
    }

    /// <summary>
    /// Resolve the caller's chat session for the loop-editor endpoints: the
    /// caller's token must be a chat's AND name a chat session that still
    /// exists. This is the loop-tool equivalent of
    /// <see cref="IAgentSessionResolver.Owns"/> — the MCP server is scoped to one session, so a
    /// token naming no session is not the caller's to act on (403).
    /// </summary>
    private async Task<(Guid ChatSessionId, IActionResult? Error)> ResolveChatSessionAsync()
    {
        if (Caller.ChatSessionId is not { } chatSessionId)
            return (Guid.Empty, BadRequest(new { error = "A chat-session id is required. Only an agent launched for a chat has one." }));

        if (!await _sessions.ChatSessionExistsAsync(chatSessionId))
            return (Guid.Empty, StatusCode(403, new { error = "Unknown or inactive chat session." }));

        return (chatSessionId, null);
    }

    [HttpPost("workitems")]
    public async Task<IActionResult> CreateWorkItem([FromBody] AgentWorkItemCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // We require a real repository here. The WorkItem entity has a
        // [Required] FK to Repository, so passing null/empty would surface
        // as a "FOREIGN KEY constraint failed" database error to the agent.
        // Convert that into a clean 400 instead.
        if (!Guid.TryParse(request.RepositoryId, out var repositoryId))
            return BadRequest(new { error = "repositoryId is required. Call list_repositories first to obtain a valid id." });
        var repoExists = await _db.Repositories.AsNoTracking().AnyAsync(r => r.Id == repositoryId);
        if (!repoExists)
            return BadRequest(new { error = $"Repository not found: {repositoryId}" });

        // A custom branch name is used verbatim as a branch and a worktree
        // directory, so an illegal one is refused rather than mangled. The base
        // branch is a ref too; whether it exists on origin is settled at run start.
        if (BranchNameRules.ValidateOverrides(request.BranchNameOverride, request.BaseBranchOverride) is { } branchError)
            return BadRequest(new { error = branchError });
        var branchNameOverride = BranchNameRules.Normalize(request.BranchNameOverride);
        var baseBranchOverride = BranchNameRules.Normalize(request.BaseBranchOverride);

        // Legacy `loopTemplateId` (if any) is ignored — template is now
        // resolved from tags at run start (PRD §3.7).

        // The originating run or chat is the caller's own, from its token. A body that names another run is refused.
        var createdByLoopRunId = Caller.RunId;
        if (Guid.TryParse(request.CreatedByLoopRunId, out var bodyRun) && bodyRun != createdByLoopRunId)
            return StatusCode(403, new { error = "createdByLoopRunId is not the calling run." });

        // A standalone Chat Session (ADR-0010) stamps through its own token.
        // It is never both: a token is for a run or a chat.
        var createdByChatSessionId = Caller.ChatSessionId;
        var createdByExternalGrantId = Caller.GrantId;

        // Validate dependencies up-front so we don't half-create.
        var dependencyIds = new List<string>();
        if (request.Dependencies is { Count: > 0 })
        {
            foreach (var raw in request.Dependencies)
            {
                var dep = raw.Trim();
                if (dep.Length == 0)
                    return BadRequest(new { error = "Dependency id cannot be empty" });
                var exists = await _workItems.GetWorkItemAsync(dep) != null;
                if (!exists)
                    return BadRequest(new { error = $"Dependency not found: {dep}" });
                dependencyIds.Add(dep);
            }
        }

        if (await _sessions.ActorAsync(Caller) is not { } actor)
            return Unauthorized();

        string id;
        try
        {
            id = await _workItems.CreateWorkItemAsync(
                actor,
                request.Title,
                request.Description ?? string.Empty,
                repositoryId,
                createdByLoopRunId,
                forceBacklog: true,
                tags: request.Tags,
                createdByChatSessionId: createdByChatSessionId,
                createdByExternalGrantId: createdByExternalGrantId,
                branchNameOverride: branchNameOverride,
                baseBranchOverride: baseBranchOverride);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        foreach (var dep in dependencyIds)
        {
            try { await _workItems.AddDependencyAsync(actor, id, dep); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        var created = await _workItems.GetWorkItemAsync(id);
        return CreatedAtAction(nameof(GetWorkItem), new { id }, new
        {
            id,
            status = created?.Status.ToString(),
            createdByLoopRunId = created?.CreatedByLoopRunId,
            createdByChatSessionId = created?.CreatedByChatSessionId,
            createdByExternalGrantId = created?.CreatedByExternalGrantId,
        });
    }

    [HttpPut("workitems/{id}")]
    public async Task<IActionResult> UpdateWorkItem(string id, [FromBody] AgentWorkItemUpdateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null) return NotFound();
        if (!_sessions.Owns(Caller, wi))
            return StatusCode(403, new { error = "You can only edit work items your own session created. To suggest an edit to this one, use propose_workitem_edit: a human approves or rejects it." });

        if (BranchNameRules.ValidateOverrides(request.BranchNameOverride, request.BaseBranchOverride) is { } branchError)
            return BadRequest(new { error = branchError });

        if (await _sessions.ActorAsync(Caller) is not { } actor)
            return Unauthorized();
        var ok = await _workItems.UpdateAsync(
            actor, id, request.Title, request.Description ?? string.Empty, request.Tags,
            branchNameOverride: request.BranchNameOverride,
            baseBranchOverride: request.BaseBranchOverride);
        if (!ok)
            return StatusCode(503, new { error = "Work item update failed." });

        var updated = await _workItems.GetWorkItemAsync(id);
        return Ok(new
        {
            id,
            title = updated?.Title,
            description = updated?.Description,
            status = updated?.Status.ToString(),
            branchNameOverride = updated?.BranchNameOverride,
            baseBranchOverride = updated?.BaseBranchOverride,
        });
    }

    [HttpDelete("workitems/{id}")]
    public async Task<IActionResult> DeleteWorkItem(string id)
    {
        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null) return NotFound();
        if (!_sessions.Owns(Caller, wi))
            return StatusCode(403, new { error = "You can only delete work items your own session created." });

        if (await _sessions.ActorAsync(Caller) is not { } actor)
            return Unauthorized();
        var ok = await _workItems.DeleteAsync(actor, id);
        if (!ok) return NotFound();
        return NoContent();
    }

    // -- Dependencies -------------------------------------------------------------
    //
    // The edge belongs to the dependent item (the one in the route), so that is
    // the item the caller must own. The other item is only pointed at.

    [HttpPost("workitems/{id}/dependencies")]
    public Task<IActionResult> AddWorkItemDependency(string id, [FromBody] AgentWorkItemDependencyRequest request)
        => ChangeWorkItemDependencyAsync(id, request.DependsOnWorkItemId, adding: true);

    [HttpDelete("workitems/{id}/dependencies/{dependsOnId}")]
    public Task<IActionResult> RemoveWorkItemDependency(string id, string dependsOnId)
        => ChangeWorkItemDependencyAsync(id, dependsOnId, adding: false);

    private async Task<IActionResult> ChangeWorkItemDependencyAsync(string id, string? rawDependsOn, bool adding)
    {
        try
        {
            var wi = await _workItems.GetWorkItemAsync(id);
            if (wi == null) return NotFound();
            if (!_sessions.Owns(Caller, wi))
                return StatusCode(403, new { error = "You can only change the dependencies of work items your own session created. To suggest this change, use propose_workitem_edit with addDependencies or removeDependencies: a human approves or rejects it." });

            var dependsOn = rawDependsOn?.Trim() ?? string.Empty;
            if (dependsOn.Length == 0)
                return BadRequest(new { error = "dependsOnWorkItemId is required." });
            if (dependsOn == id)
                return BadRequest(new { error = "A work item cannot depend on itself." });
            if (await _workItems.GetWorkItemAsync(dependsOn) == null)
                return BadRequest(new { error = $"Dependency not found: {dependsOn}" });

            var before = await _workItems.GetDependenciesAsync(id);
            if (before.Any(d => d.Id == dependsOn) == adding)
            {
                var unchanged = adding
                    ? $"Nothing changed: work item {id} already depends on {dependsOn}."
                    : $"Nothing changed: work item {id} does not depend on {dependsOn}.";
                return Ok(DependencyChange(id, dependsOn, changed: false, unchanged, before));
            }

            if (await _sessions.ActorAsync(Caller) is not { } actor)
                return Unauthorized();
            bool ok;
            try
            {
                ok = adding
                    ? await _workItems.AddDependencyAsync(actor, id, dependsOn)
                    : await _workItems.RemoveDependencyAsync(actor, id, dependsOn);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            if (!ok)
                return StatusCode(503, new { error = "Work item dependency change failed." });

            var message = adding
                ? $"Work item {id} now depends on {dependsOn}."
                : $"Work item {id} no longer depends on {dependsOn}.";
            return Ok(DependencyChange(id, dependsOn, changed: true, message, await _workItems.GetDependenciesAsync(id)));
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    private static object DependencyChange(
        string id, string dependsOn, bool changed, string message, IReadOnlyList<WorkItemView> dependencies) => new
    {
        workItemId = id,
        dependsOnWorkItemId = dependsOn,
        changed,
        message,
        dependencies = dependencies.Select(d => new { id = d.Id, title = d.Title, status = d.Status.ToString() }),
    };

    // -- Work Item Edit Proposals ---------------------------------------------
    //
    // The way an agent suggests an edit to an item its session did not create.
    // Proposing applies nothing; approving and rejecting are human-only and live
    // on WorkItemsController, so nothing on this surface can decide a proposal.

    [HttpPost("workitems/{id}/edit-proposals")]
    public async Task<IActionResult> ProposeWorkItemEdit(
        string id,
        [FromBody] AgentWorkItemEditProposalRequest request,
        [FromServices] IWorkItemEditProposalService proposals,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (proposer, sessionError) = await ResolveProposingSessionAsync();
        if (sessionError is not null)
            return sessionError;

        WorkItemEditProposalResult result;
        try
        {
            result = await proposals.ProposeAsync(id, request, Caller, proposer!, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }

        switch (result.Outcome)
        {
            case WorkItemEditProposalOutcome.Created:
                return CreatedAtAction(nameof(ListWorkItemEditProposals), new { id }, new
                {
                    id = result.Proposal!.Id,
                    workItemId = id,
                    status = result.Proposal.Status.ToString(),
                });
            case WorkItemEditProposalOutcome.NotFound:
                return NotFound();
            case WorkItemEditProposalOutcome.Unauthorized:
                return Unauthorized();
            case WorkItemEditProposalOutcome.TooManyPending:
                return Conflict(new { error = result.Error });
            default:
                return BadRequest(new { error = result.Error });
        }
    }

    /// <summary>
    /// What became of the proposals on a work item — how an agent learns a
    /// rejection's reason, which for a loop run has no next chat turn to carry it.
    /// </summary>
    [HttpGet("workitems/{id}/edit-proposals")]
    public async Task<IActionResult> ListWorkItemEditProposals(string id, CancellationToken cancellationToken)
    {
        try
        {
            var proposals = await _workItems.ListEditProposalsAsync(id, cancellationToken);
            return proposals == null ? NotFound() : Ok(proposals);
        }
        catch (HttpRequestException ex)
        {
            return WorkItemServerUnreachable(ex);
        }
    }

    // -- Global Variable Proposals (ADR-0027) ---------------------------------
    //
    // Agents read the global variables and suggest a whole new value for one.
    // Approving and rejecting are human-only and live on GlobalVariablesController.

    [HttpGet("global-variables")]
    public async Task<IActionResult> ListGlobalVariables(
        [FromServices] IGlobalVariableStore globals, CancellationToken cancellationToken)
        => Ok((await globals.GetAllAsync(cancellationToken)).Select(v => new
        {
            name = v.Name,
            value = v.Value,
            updatedAt = v.UpdatedAt,
        }));

    [HttpPost("global-variables/proposals")]
    public async Task<IActionResult> ProposeGlobalVariableChange(
        [FromBody] AgentGlobalVariableProposalRequest request,
        [FromServices] IGlobalVariableProposalService proposals,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (proposer, sessionError) = await ResolveProposingSessionAsync();
        if (sessionError is not null)
            return sessionError;

        var result = await proposals.ProposeAsync(request, proposer!, cancellationToken);
        if (result.Outcome == GlobalVariableProposalOutcome.Invalid)
            return BadRequest(new { error = result.Error });

        var proposal = result.Proposal!;
        return CreatedAtAction(nameof(ListGlobalVariableProposals), null, new
        {
            id = proposal.Id,
            name = proposal.Name,
            status = proposal.Status.ToString(),
        });
    }

    /// <summary>
    /// What became of the calling run's or chat's own suggestions — how a loop
    /// run learns a decision, having no next chat turn to carry it.
    /// </summary>
    [HttpGet("global-variables/proposals")]
    public async Task<IActionResult> ListGlobalVariableProposals(
        [FromServices] IGlobalVariableProposalStore proposals, CancellationToken cancellationToken)
    {
        var (proposer, sessionError) = await ResolveProposingSessionAsync();
        if (sessionError is not null)
            return sessionError;

        var own = await proposals.ListAsync(
            new GlobalVariableProposalQuery(CreatedByLoopRunId: proposer!.RunId, CreatedByChatSessionId: proposer.ChatSessionId, CreatedByExternalGrantId: proposer.GrantId),
            cancellationToken);
        return Ok(own.Select(e => new
        {
            id = e.Proposal.Id,
            name = e.Proposal.Name,
            value = e.Proposal.Value,
            rationale = e.Proposal.Rationale,
            status = e.Proposal.Status.ToString(),
            rejectionReason = e.Proposal.RejectionReason,
            createdAt = e.Proposal.CreatedAt,
            decidedAt = e.Proposal.DecidedAt,
        }));
    }

    // -- Human Input Proposals (ADR-0030) -------------------------------------
    //
    // Agents suggest the answer for a work item parked at a Human or PR node. Approving
    // and rejecting are human-only and live on HumanInputProposalsController.

    [HttpPost("workitems/{id}/human-input-proposals")]
    public async Task<IActionResult> ProposeHumanInput(
        string id,
        [FromBody] AgentHumanInputProposalRequest request,
        [FromServices] IHumanInputProposalService proposals,
        CancellationToken cancellationToken)
    {
        var (proposer, sessionError) = await ResolveProposingSessionAsync();
        if (sessionError is not null)
            return sessionError;

        var output = AppDbContext.WithoutNul(request.Output ?? string.Empty).Trim();
        if (output.Length == 0)
            return BadRequest(new { error = "Name the output to take: OnSuccess, OnFailure or the name of one of the node's outputs." });
        // Checked as they will be stored: the save drops NUL characters.
        var text = AppDbContext.WithoutNul(request.Text ?? string.Empty);
        if (text.Length > HumanInputProposal.MaxTextLength)
            return BadRequest(new { error = $"The text is {text.Length:N0} / {HumanInputProposal.MaxTextLength:N0} characters; shorten it and suggest it again." });
        var rationale = AppDbContext.WithoutNul(request.Rationale ?? string.Empty).Trim();
        if (rationale.Length == 0)
            return BadRequest(new { error = "Give a rationale: why this is the answer." });
        if (rationale.Length > HumanInputProposal.MaxRationaleLength)
            return BadRequest(new { error = $"The rationale is {rationale.Length:N0} / {HumanInputProposal.MaxRationaleLength:N0} characters; shorten it." });

        var result = await proposals.ProposeAsync(id, output, text, rationale, proposer!.RunId, proposer.ChatSessionId, cancellationToken, proposer.GrantId);
        return result.Outcome switch
        {
            HumanInputProposalOutcome.Created => StatusCode(StatusCodes.Status201Created, new
            {
                id = result.Proposal!.Id,
                workItemId = result.Proposal.WorkItemId,
                output = result.Proposal.Output,
                status = result.Proposal.Status.ToString(),
            }),
            HumanInputProposalOutcome.NotFound => NotFound(),
            HumanInputProposalOutcome.NotParked => Conflict(new { error = "This work item is not waiting for human input at a Human or PR node." }),
            _ => BadRequest(new
            {
                error = $"The node does not offer the output '{output}'. It offers: {string.Join(", ", result.OfferedOutputs!)}.",
            }),
        };
    }

    // -- Loop Template Proposals (ADR-0028) ----------------------------------
    //
    // Agents propose a whole new document, or an Edit List, for a saved template
    // against its latest version. Saving and rejecting are human-only and live on
    // LoopTemplateProposalsController; saving from review is a LoopTemplatesController save.

    [HttpPost("loop-templates/proposals")]
    public async Task<IActionResult> ProposeLoopTemplateChange(
        [FromBody] AgentLoopTemplateProposalRequest request,
        [FromServices] ILoopTemplateProposalService proposals,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (proposer, sessionError) = await ResolveProposingSessionAsync();
        if (sessionError is not null)
            return sessionError;

        var result = await proposals.ProposeAsync(request, proposer!, cancellationToken);
        switch (result.Outcome)
        {
            case LoopTemplateProposalOutcome.Created:
                var proposal = result.Proposal!;
                return CreatedAtAction(nameof(ListLoopTemplateProposals), null, new
                {
                    id = proposal.Id,
                    templateId = proposal.LoopTemplateId,
                    baseVersion = proposal.BaseVersion,
                    status = proposal.Status.ToString(),
                });
            case LoopTemplateProposalOutcome.TemplateNotFound:
                return NotFound(new { error = result.Error });
            case LoopTemplateProposalOutcome.AmbiguousName:
                return AmbiguousLoopTemplateName(result.Error!, result.Candidates!);
            case LoopTemplateProposalOutcome.NotLatest:
                return Conflict(new { error = result.Error, latestVersion = result.LatestVersion });
            default:
                if (result.EditIndex is { } editIndex)
                    return BadRequest(new { error = result.Error, editIndex });
                if (result.ValidationErrors is { } validationErrors)
                    return BadRequest(new { error = result.Error, validationErrors });
                return BadRequest(new { error = result.Error });
        }
    }

    /// <summary>
    /// What became of the calling run's or chat's own proposals — how a loop run
    /// learns a decision, having no next chat turn to carry it.
    /// </summary>
    [HttpGet("loop-templates/proposals")]
    public async Task<IActionResult> ListLoopTemplateProposals(
        [FromServices] ILoopTemplateProposalStore proposals, CancellationToken cancellationToken)
    {
        var (proposer, sessionError) = await ResolveProposingSessionAsync();
        if (sessionError is not null)
            return sessionError;

        var own = await proposals.ListAsync(
            new LoopTemplateProposalQuery(CreatedByLoopRunId: proposer!.RunId, CreatedByChatSessionId: proposer.ChatSessionId, CreatedByExternalGrantId: proposer.GrantId),
            cancellationToken);
        return Ok(own.Select(e => new
        {
            id = e.Proposal.Id,
            templateId = e.Proposal.LoopTemplateId,
            templateName = e.Template?.Name,
            baseVersion = e.Proposal.BaseVersion,
            rationale = e.Proposal.Rationale,
            editCount = e.Proposal.Edits is null ? (int?)null : JsonNode.Parse(e.Proposal.Edits)!.AsArray().Count,
            status = e.Proposal.Status.ToString(),
            editedBeforeSave = e.Proposal.EditedBeforeSave,
            appliedVersion = e.Proposal.AppliedVersion,
            rejectionReason = e.Proposal.RejectionReason,
            createdAt = e.Proposal.CreatedAt,
            decidedAt = e.Proposal.DecidedAt,
        }));
    }

    private AgentCaller Caller => new(
        IsAgent: User.IsInRole(IldAuthentication.AgentRole),
        IsExternalAgent: User.IsInRole(IldAuthentication.ExternalAgentRole),
        SessionSource: User.FindFirstValue(IldAuthentication.SessionSourceClaim),
        SessionId: Guid.TryParse(User.FindFirstValue(IldAuthentication.SessionIdClaim), out var sessionId) ? sessionId : null,
        GrantScope: User.FindFirstValue(IldAuthentication.GrantScopeClaim),
        UserName: User.Identity?.Name);

    private async Task<(AgentProposer? Proposer, IActionResult? Error)> ResolveProposingSessionAsync()
    {
        var resolution = await _sessions.ResolveProposerAsync(Caller);
        return resolution.Outcome switch
        {
            ProposerOutcome.Resolved => (resolution.Proposer, null),
            ProposerOutcome.Unknown => (null, StatusCode(403, new { error = resolution.Error })),
            _ => (null, BadRequest(new { error = resolution.Error })),
        };
    }

    private async Task<IActionResult?> CallerMayTouchAsync(string workItemId)
        => await _sessions.ReachRefusalAsync(Caller, workItemId) is { } refusal
            ? StatusCode(403, new { error = refusal })
            : null;

    private static ObjectResult WorkItemServerUnreachable(HttpRequestException ex)
        => new(new { error = "WorkItemServer unreachable", detail = ex.Message }) { StatusCode = 503 };
}
