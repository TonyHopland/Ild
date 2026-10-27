using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// The suggestions agents made for the question a work item is parked on at a
/// Human node (ADR-0030), which only a human approves or rejects. User-only,
/// like everything outside the agent API.
/// </summary>
[ApiController]
[Route("api/v1/human-input-proposals")]
public class HumanInputProposalsController : ControllerBase
{
    private readonly IHumanInputProposalService _service;
    private readonly IHumanInputProposalStore _proposals;

    public HumanInputProposalsController(
        IHumanInputProposalService service, IHumanInputProposalStore proposals)
    {
        _service = service;
        _proposals = proposals;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? workItemId, [FromQuery] string? status, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId))
            return BadRequest(new { error = "workItemId is required." });
        HumanInputProposalStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<HumanInputProposalStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                return BadRequest(new { error = $"Unknown suggestion status: {status}" });
            parsedStatus = s;
        }

        return Ok((await _proposals.ListForWorkItemAsync(workItemId, parsedStatus, ct)).Select(View));
    }

    /// <summary>Delivers the suggestion exactly as it was made; the request carries no text.</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var result = await _service.ApproveAsync(id, DecidedBy, ct);
        return result.Outcome switch
        {
            HumanInputDecisionOutcome.Approved => Ok(new { proposal = View(result.Proposal!) }),
            HumanInputDecisionOutcome.Stale => Conflict(new
            {
                error = "The question this suggestion answers is no longer open, so nothing was sent. The suggestion is now stale.",
                proposal = View(result.Proposal!),
            }),
            _ => DecisionRefused(result),
        };
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectEditProposalRequest request, CancellationToken ct)
    {
        var result = await _service.RejectAsync(id, request.Reason, DecidedBy, ct);
        return result.Outcome == HumanInputDecisionOutcome.Rejected ? Ok(View(result.Proposal!)) : DecisionRefused(result);
    }

    private string DecidedBy => User.Identity?.Name ?? string.Empty;

    private IActionResult DecisionRefused(HumanInputProposalDecision result)
        => result.Proposal is { } proposal
            ? Conflict(new { error = $"This suggestion was already decided ({proposal.Status}).", proposal = View(proposal) })
            : NotFound(new { error = "No such suggestion." });

    private static object View(HumanInputProposal p) => new
    {
        id = p.Id,
        workItemId = p.WorkItemId,
        runId = p.RunId,
        runNodeId = p.RunNodeId,
        output = p.Output,
        text = p.Text,
        rationale = p.Rationale,
        status = p.Status.ToString(),
        rejectionReason = p.RejectionReason,
        decidedBy = p.DecidedBy,
        createdAt = p.CreatedAt,
        decidedAt = p.DecidedAt,
        createdByLoopRunId = p.CreatedByLoopRunId,
        createdByChatSessionId = p.CreatedByChatSessionId,
    };
}
