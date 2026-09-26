using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ILD.WorkItemServer.Controllers;

/// <summary>
/// Agent-proposed edits to work items and the human decisions on them. Who may
/// propose and who may decide is ILD's call; this server keeps the rows and
/// makes approving one atomic compare-and-apply.
/// </summary>
[ApiController]
public sealed class WorkItemEditProposalsController : ControllerBase
{
    private readonly IWorkItemEditProposalService _proposals;

    public WorkItemEditProposalsController(IWorkItemEditProposalService proposals) => _proposals = proposals;

    [HttpPost("workitems/{id}/edit-proposals")]
    public async Task<IActionResult> Create(string id, [FromBody] CreateEditProposalRequest req, CancellationToken ct)
    {
        var result = await _proposals.CreateAsync(id, req, ct);
        return result.Outcome switch
        {
            EditProposalCreateOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Proposal),
            EditProposalCreateOutcome.NotFound => NotFound(),
            EditProposalCreateOutcome.TooManyPending => Conflict(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpGet("workitems/{id}/edit-proposals")]
    public async Task<ActionResult<IReadOnlyList<WorkItemEditProposalDto>>> ListForItem(string id, CancellationToken ct)
    {
        var listed = await _proposals.ListForItemAsync(id, ct);
        return listed == null ? NotFound() : Ok(listed);
    }

    [HttpGet("edit-proposals")]
    public async Task<ActionResult<IReadOnlyList<WorkItemEditProposalDto>>> List(
        [FromQuery] WorkItemEditProposalStatus? status,
        [FromQuery] Guid? createdByChatSessionId,
        [FromQuery] bool undelivered,
        CancellationToken ct)
        => Ok(await _proposals.ListAsync(status, createdByChatSessionId, undelivered, ct));

    [HttpPost("workitems/{id}/edit-proposals/{proposalId:guid}/approve")]
    public async Task<IActionResult> Approve(string id, Guid proposalId, CancellationToken ct)
        => ToActionResult(await _proposals.ApproveAsync(id, proposalId, ct));

    [HttpPost("workitems/{id}/edit-proposals/{proposalId:guid}/reject")]
    public async Task<IActionResult> Reject(string id, Guid proposalId, [FromBody] RejectEditProposalRequest req, CancellationToken ct)
    {
        if (req.Reason?.Trim().Length > WorkItemEditProposalService.MaxRejectionReasonLength)
            return BadRequest(new { error = $"A reason may be at most {WorkItemEditProposalService.MaxRejectionReasonLength} characters." });
        return ToActionResult(await _proposals.RejectAsync(id, proposalId, req.Reason, ct));
    }

    [HttpPost("edit-proposals/delivered")]
    public async Task<IActionResult> MarkDelivered([FromBody] MarkEditProposalsDeliveredRequest req, CancellationToken ct)
    {
        await _proposals.MarkDecisionsDeliveredAsync(req.Ids, ct);
        return NoContent();
    }

    /// <summary>
    /// A refused decision is a 409 that still carries the proposal and says why,
    /// so a client can tell a stale proposal from one already decided.
    /// </summary>
    private IActionResult ToActionResult(EditProposalDecisionResult result)
    {
        var body = new EditProposalDecisionResponse
        {
            Outcome = result.Outcome,
            Proposal = result.Proposal,
            WorkItem = result.WorkItem,
        };
        switch (result.Outcome)
        {
            case EditProposalDecisionOutcome.Applied:
            case EditProposalDecisionOutcome.Rejected:
                return Ok(body);
            case EditProposalDecisionOutcome.Stale:
                body.Error = "The work item changed after this proposal was made, so nothing was applied.";
                return Conflict(body);
            case EditProposalDecisionOutcome.NotPending:
                body.Error = $"This proposal was already decided ({result.Proposal?.Status}).";
                return Conflict(body);
            default:
                return NotFound();
        }
    }
}
