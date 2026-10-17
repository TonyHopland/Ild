using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// The instance's global variables, which loop prompts read with
/// <c>{{Global.&lt;name&gt;}}</c>. Each comes with the live loop templates whose
/// latest version uses it, and the suggestions agents made for them (ADR-0027),
/// which only a human approves or rejects here. User-only, like everything
/// outside the agent API.
/// </summary>
[ApiController]
[Route("api/v1/global-variables")]
public class GlobalVariablesController : ControllerBase
{
    private readonly IGlobalVariableStore _variables;
    private readonly ILoopTemplateStore _templates;
    private readonly ISchedulerSettingsService _settings;
    private readonly IGlobalVariableProposalStore _proposals;
    private readonly IWorkItemNotifier _notifier;
    private readonly IChatNotifier _chatNotifier;

    public GlobalVariablesController(
        IGlobalVariableStore variables,
        ILoopTemplateStore templates,
        ISchedulerSettingsService settings,
        IGlobalVariableProposalStore proposals,
        IWorkItemNotifier notifier,
        IChatNotifier chatNotifier)
    {
        _variables = variables;
        _templates = templates;
        _settings = settings;
        _proposals = proposals;
        _notifier = notifier;
        _chatNotifier = chatNotifier;
    }

    public sealed class CreateVariableRequest
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }

    /// <summary>The name is the variable's identity (templates use it by name), so an update cannot change it.</summary>
    public sealed class UpdateVariableRequest
    {
        public string? Value { get; set; }
    }

    private sealed record TemplateRef(Guid Id, string Name);

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var usage = await UsageAsync();
        return Ok((await _variables.GetAllAsync(ct)).Select(v => View(v, usage)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVariableRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { error = "Give the variable a name, e.g. review_rules" });
        if (!PromptPlaceholderRegistry.IsValidVariableName(name))
            return BadRequest(new { error = "A name is a letter followed by up to 127 letters, digits or underscores" });
        if (await ValueProblemAsync(request.Value, ct) is { } problem)
            return BadRequest(new { error = problem });

        var normalized = GlobalVariable.Normalize(name);
        if ((await _variables.GetAllAsync(ct)).Any(v => v.NormalizedName == normalized))
            return BadRequest(new { error = NameTaken(name) });

        var variable = new GlobalVariable
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = normalized,
            Value = request.Value!,
            UpdatedAt = DateTime.UtcNow,
        };
        StaledProposals staled;
        try
        {
            staled = await _variables.AddAsync(variable, ct);
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two saves racing.
            return BadRequest(new { error = NameTaken(name) });
        }
        await HintAsync(variable.Name, staled.Count, staled.ChatSessionIds);
        return CreatedAtAction(nameof(GetAll), View(variable, await UsageAsync()));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVariableRequest request, CancellationToken ct)
    {
        var variable = await _variables.GetAsync(id, ct);
        if (variable is null) return NotFound(new { error = Gone });
        if (await ValueProblemAsync(request.Value, ct) is { } problem)
            return BadRequest(new { error = problem });

        variable.Value = request.Value!;
        variable.UpdatedAt = DateTime.UtcNow;
        StaledProposals staled;
        try
        {
            staled = await _variables.UpdateAsync(variable, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted between the read above and this save.
            return NotFound(new { error = Gone });
        }
        await HintAsync(variable.Name, staled.Count, staled.ChatSessionIds);
        return Ok(View(variable, await UsageAsync()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var variable = await _variables.GetAsync(id, ct);
        if (variable is null || await _variables.DeleteAsync(id, ct) is not { } staled)
            return NotFound(new { error = Gone });
        await HintAsync(variable.Name, staled.Count, staled.ChatSessionIds);
        return NoContent();
    }

    [HttpGet("proposals")]
    public async Task<IActionResult> ListProposals(
        [FromQuery] string? status, [FromQuery] string? workItemId, [FromQuery] string? chatSessionId, CancellationToken ct)
    {
        GlobalVariableProposalStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<GlobalVariableProposalStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                return BadRequest(new { error = $"Unknown suggestion status: {status}" });
            parsedStatus = s;
        }
        Guid? parsedChat = null;
        if (!string.IsNullOrEmpty(chatSessionId))
        {
            if (!Guid.TryParse(chatSessionId, out var c))
                return BadRequest(new { error = "chatSessionId must be a GUID." });
            parsedChat = c;
        }

        var entries = await _proposals.ListAsync(new GlobalVariableProposalQuery(
            Status: parsedStatus,
            CreatedByChatSessionId: parsedChat,
            WorkItemId: string.IsNullOrEmpty(workItemId) ? null : workItemId), ct);
        return Ok(entries.Select(ProposalView));
    }

    [HttpGet("proposals/pending-count")]
    public async Task<IActionResult> PendingProposalCount(CancellationToken ct)
        => Ok(new { count = await _proposals.CountPendingAsync(ct) });

    /// <summary>Applies the suggestion exactly as it was made; the request carries no value.</summary>
    [HttpPost("proposals/{proposalId:guid}/approve")]
    public async Task<IActionResult> ApproveProposal(Guid proposalId, CancellationToken ct)
    {
        var result = await _proposals.ApproveAsync(proposalId, ct);
        switch (result.Outcome)
        {
            case GlobalVariableProposalDecisionOutcome.Approved:
                await HintAsync(result.Entry!.Proposal.Name, 1, result.ChangedChatSessionIds);
                return Ok(new
                {
                    proposal = ProposalView(result.Entry),
                    variable = result.Entry.Variable is { } variable ? View(variable, await UsageAsync()) : null,
                });
            case GlobalVariableProposalDecisionOutcome.Stale:
                await HintAsync(result.Entry!.Proposal.Name, 1, result.ChangedChatSessionIds);
                return Conflict(new
                {
                    error = "The variable changed after this suggestion was made, so nothing was applied. The suggestion is now stale.",
                    proposal = ProposalView(result.Entry),
                });
            default:
                return DecisionRefused(result);
        }
    }

    [HttpPost("proposals/{proposalId:guid}/reject")]
    public async Task<IActionResult> RejectProposal(Guid proposalId, [FromBody] RejectEditProposalRequest request, CancellationToken ct)
    {
        var result = await _proposals.RejectAsync(proposalId, request.Reason, ct);
        if (result.Outcome != GlobalVariableProposalDecisionOutcome.Rejected)
            return DecisionRefused(result);
        await HintAsync(result.Entry!.Proposal.Name, 1, result.ChangedChatSessionIds);
        return Ok(ProposalView(result.Entry));
    }

    private IActionResult DecisionRefused(GlobalVariableProposalDecision result)
        => result.Entry is { } entry
            ? Conflict(new { error = $"This suggestion was already decided ({entry.Proposal.Status}).", proposal = ProposalView(entry) })
            : NotFound(new { error = "No such suggestion." });

    /// <summary>Hints only, sent once the change has landed and only when a suggestion changed.</summary>
    private async Task HintAsync(string name, int changed, IReadOnlyList<Guid> chatSessionIds)
    {
        if (changed == 0) return;
        await _notifier.GlobalVariableProposalsChangedAsync(name);
        foreach (var chat in chatSessionIds)
            await _chatNotifier.GlobalVariableProposalsChangedAsync(chat);
    }

    /// <summary>
    /// The limit is checked on every write, never on what is stored, so lowering
    /// it leaves a longer value alone until its next edit.
    /// </summary>
    private async Task<string?> ValueProblemAsync(string? value, CancellationToken ct)
    {
        if (value is null) return "Give the variable a value; it may be empty";
        var limit = await _settings.GetGlobalVariableMaxLengthAsync(ct);
        return value.Length > limit ? $"The value is {value.Length} characters; the limit is {limit}." : null;
    }

    /// <summary>
    /// The live templates using each global variable, keyed by its normalized
    /// name: those whose latest version has a node config naming it.
    /// </summary>
    private async Task<ILookup<string, TemplateRef>> UsageAsync()
        => (await _templates.GetLatestNodeConfigsOfLiveTemplatesAsync())
            .SelectMany(n => PromptPlaceholderRegistry.GlobalVariableNames(n.Config)
                .Select(name => (Name: GlobalVariable.Normalize(name), Template: new TemplateRef(n.TemplateId, n.TemplateName))))
            .Distinct()
            .ToLookup(u => u.Name, u => u.Template, StringComparer.Ordinal);

    private const string Gone = "No such variable; it may have been deleted.";

    private static string NameTaken(string name) => $"A variable named '{name}' already exists";

    /// <summary>
    /// A suggestion as the page shows it: against the variable as it is now, and
    /// with where it came from. A source that is gone is "deleted", never guessed;
    /// another user's chat is named but not linkable, since chats are owner-only.
    /// </summary>
    private object ProposalView(GlobalVariableProposalEntry entry)
    {
        var p = entry.Proposal;
        var kind = entry.SourceWorkItemId is not null ? "run" : entry.SourceChatOwner is not null ? "chat" : "deleted";
        return new
        {
            id = p.Id,
            name = p.Name,
            value = p.Value,
            rationale = p.Rationale,
            status = p.Status.ToString(),
            rejectionReason = p.RejectionReason,
            createdAt = p.CreatedAt,
            decidedAt = p.DecidedAt,
            snapshotUpdatedAt = p.SnapshotUpdatedAt,
            currentValue = entry.Variable?.Value,
            variableExists = entry.Variable is not null,
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
        };
    }

    private static object View(GlobalVariable v, ILookup<string, TemplateRef> usage) => new
    {
        id = v.Id,
        name = v.Name,
        value = v.Value,
        updatedAt = v.UpdatedAt,
        usedBy = usage[v.NormalizedName].Select(t => new { id = t.Id, name = t.Name }),
    };
}
