using System.Diagnostics;
using ILD.Api.Configuration;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// The changes agents proposed to saved loop templates (ADR-0028), which only a
/// human saves or rejects here; reviewing one in the Loop Editor ends in an
/// ordinary save on <c>LoopTemplatesController</c>. User-only, like everything
/// outside the agent API.
/// </summary>
[ApiController]
[Route("api/v1/loop-template-proposals")]
public class LoopTemplateProposalsController : ControllerBase
{
    private readonly ILoopTemplateProposalStore _proposals;
    private readonly ILoopTemplateManager _templates;
    private readonly IWorkItemNotifier _notifier;
    private readonly IChatNotifier _chatNotifier;

    public LoopTemplateProposalsController(
        ILoopTemplateProposalStore proposals,
        ILoopTemplateManager templates,
        IWorkItemNotifier notifier,
        IChatNotifier chatNotifier)
    {
        _proposals = proposals;
        _templates = templates;
        _notifier = notifier;
        _chatNotifier = chatNotifier;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? templateId, [FromQuery] string? workItemId,
        [FromQuery] string? chatSessionId, CancellationToken ct)
    {
        LoopTemplateProposalStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<LoopTemplateProposalStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                return BadRequest(new { error = $"Unknown proposal status: {status}" });
            parsedStatus = s;
        }
        Guid? parsedTemplate = null;
        if (!string.IsNullOrEmpty(templateId))
        {
            if (!Guid.TryParse(templateId, out var t))
                return BadRequest(new { error = "templateId must be a GUID." });
            parsedTemplate = t;
        }
        Guid? parsedChat = null;
        if (!string.IsNullOrEmpty(chatSessionId))
        {
            if (!Guid.TryParse(chatSessionId, out var c))
                return BadRequest(new { error = "chatSessionId must be a GUID." });
            parsedChat = c;
        }

        var entries = await _proposals.ListAsync(new LoopTemplateProposalQuery(
            Status: parsedStatus,
            TemplateId: parsedTemplate,
            CreatedByChatSessionId: parsedChat,
            WorkItemId: string.IsNullOrEmpty(workItemId) ? null : workItemId), ct);
        return Ok(entries.Select(e => View(e, withDocument: false)));
    }

    [HttpGet("pending-counts")]
    public async Task<IActionResult> PendingCounts(CancellationToken ct)
        => Ok(await _proposals.CountPendingByTemplateAsync(ct));

    [HttpGet("{proposalId:guid}")]
    public async Task<IActionResult> Get(Guid proposalId, CancellationToken ct)
        => await _proposals.GetAsync(proposalId, ct) is { } entry
            ? Ok(View(entry, withDocument: true))
            : NotFound(new { error = NoSuchProposal });

    /// <summary>Saves the proposed document exactly as it was proposed; the request carries no loop.</summary>
    [HttpPost("{proposalId:guid}/save")]
    public async Task<IActionResult> Save(Guid proposalId, CancellationToken ct)
    {
        var entry = await _proposals.GetAsync(proposalId, ct);
        if (entry is null)
            return NotFound(new { error = NoSuchProposal });
        if (entry.Proposal.Status != LoopTemplateProposalStatus.Pending)
            return AlreadyDecided(entry);

        LoopTemplateSaveResult saved;
        try
        {
            saved = await _templates.SaveProposalAsync(entry.Proposal);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await LoopTemplateProposalHints.SendAsync(_notifier, _chatNotifier, entry.Proposal.LoopTemplateId, saved.ProposalsChanged, saved.ChangedChatSessionIds);

        var after = await _proposals.GetAsync(proposalId, ct);
        if (after is null)
            return NotFound(new { error = NoSuchProposal });
        return saved.Outcome switch
        {
            LoopTemplateSaveOutcome.Saved => Ok(new { proposal = View(after, withDocument: false), version = saved.VersionNumber }),
            LoopTemplateSaveOutcome.Conflict => Conflict(new
            {
                error = "The template has a newer version than the one this proposal was made against, so nothing was saved. The proposal is now stale.",
                proposal = View(after, withDocument: false),
            }),
            LoopTemplateSaveOutcome.ProposalNotPending => AlreadyDecided(after),
            LoopTemplateSaveOutcome.TemplateNotFound => NotFound(new { error = "The loop template this proposal was made for no longer exists." }),
            _ => throw new UnreachableException($"Unhandled save outcome {saved.Outcome}."),
        };
    }

    [HttpPost("{proposalId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid proposalId, [FromBody] RejectEditProposalRequest request, CancellationToken ct)
    {
        var result = await _proposals.RejectAsync(proposalId, request.Reason, ct);
        if (result.Entry is not { } entry)
            return NotFound(new { error = NoSuchProposal });
        if (result.Outcome != LoopTemplateProposalDecisionOutcome.Rejected)
            return AlreadyDecided(entry);
        await LoopTemplateProposalHints.SendAsync(_notifier, _chatNotifier, entry.Proposal.LoopTemplateId, 1, result.ChangedChatSessionIds);
        return Ok(View(entry, withDocument: false));
    }

    private const string NoSuchProposal = "No such loop template proposal.";

    private IActionResult AlreadyDecided(LoopTemplateProposalEntry entry)
        => Conflict(new
        {
            error = $"This proposal was already decided ({entry.Proposal.Status}).",
            proposal = View(entry, withDocument: false),
        });

    /// <summary>
    /// A proposal as a person reviews it: against its base version, with the
    /// template as it is now, and with where it came from. A source that is gone
    /// is "deleted", never guessed; another user's chat is named but not
    /// linkable, since chats are owner-only.
    /// </summary>
    private object View(LoopTemplateProposalEntry entry, bool withDocument)
    {
        var p = entry.Proposal;
        var kind = entry.SourceWorkItemId is not null ? "run" : entry.SourceChatOwner is not null ? "chat" : "deleted";
        return new
        {
            id = p.Id,
            templateId = p.LoopTemplateId,
            templateName = entry.Template?.Name,
            templateExists = entry.Template is not null,
            baseVersion = p.BaseVersion,
            latestVersion = entry.LatestVersion,
            rationale = p.Rationale,
            status = p.Status.ToString(),
            editedBeforeSave = p.EditedBeforeSave,
            appliedVersion = p.AppliedVersion,
            rejectionReason = p.RejectionReason,
            createdAt = p.CreatedAt,
            decidedAt = p.DecidedAt,
            createdByLoopRunId = p.CreatedByLoopRunId,
            createdByRunNodeId = p.CreatedByRunNodeId,
            createdByChatSessionId = p.CreatedByChatSessionId,
            chatReplySequence = p.ChatReplySequence,
            source = new
            {
                kind,
                workItemId = entry.SourceWorkItemId,
                chatSessionId = kind == "chat" ? p.CreatedByChatSessionId : null,
                linkable = kind == "run" || (kind == "chat" && entry.SourceChatOwner == User.Identity?.Name),
            },
            changes = entry.BaseGraph is { } baseGraph ? LoopTemplateChanges.Diff(baseGraph, p.Document) : null,
            document = withDocument ? p.Document : null,
        };
    }
}
