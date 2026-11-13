using System.ComponentModel.DataAnnotations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// Where the operator hands an AI session outside ILD temporary access (ADR-0037). Every route here is the
/// operator's alone: it falls under the user-only fallback policy, so a grant token cannot create, list or
/// revoke grants, its own included.
/// </summary>
[ApiController]
[Route("api/v1/external-grants")]
public class ExternalGrantsController : ControllerBase
{
    private const int RecentActivity = 50;

    private readonly IExternalAgentGrantService _grants;
    private readonly IWorkItemManager _workItems;
    private readonly IGlobalVariableProposalStore _globalProposals;
    private readonly ILoopTemplateProposalStore _templateProposals;
    private readonly AppDbContext _db;

    public ExternalGrantsController(
        IExternalAgentGrantService grants,
        IWorkItemManager workItems,
        IGlobalVariableProposalStore globalProposals,
        ILoopTemplateProposalStore templateProposals,
        AppDbContext db)
    {
        _grants = grants;
        _workItems = workItems;
        _globalProposals = globalProposals;
        _templateProposals = templateProposals;
        _db = db;
    }

    public sealed class CreateGrantRequest
    {
        [Required]
        public string? Name { get; set; }

        /// <summary>How long the grant lasts, in minutes; at most <see cref="IExternalAgentGrantService.MaxLifetime"/>.</summary>
        public int LifetimeMinutes { get; set; }

        public ExternalAgentGrantScope Scope { get; set; } = ExternalAgentGrantScope.Full;
    }

    /// <summary>The token is in the response of this call and of no other, ever.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGrantRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { error = "Give the grant a name, e.g. the AI session it is for." });
        if (name.Length > IExternalAgentGrantService.MaxNameLength)
            return BadRequest(new { error = $"A name may be at most {IExternalAgentGrantService.MaxNameLength} characters." });

        var lifetime = TimeSpan.FromMinutes(request.LifetimeMinutes);
        if (IExternalAgentGrantService.ValidateLifetime(lifetime) is { } problem)
            return BadRequest(new { error = problem });

        if (User.Identity?.Name is not { Length: > 0 } createdBy)
            return Unauthorized();

        var issued = await _grants.CreateAsync(name, lifetime, request.Scope, createdBy, ct);
        return CreatedAtAction(nameof(Get), new { id = issued.Grant.Id }, new { grant = View(issued.Grant), token = issued.Token });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok((await _grants.ListAsync(ct)).Select(View));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (await _grants.GetAsync(id, ct) is not { } grant)
            return NotFound();

        var activity = await _grants.ListActivityAsync(id, RecentActivity, ct);
        return Ok(new
        {
            grant = View(grant),
            pendingProposals = await PendingProposalsAsync(id, ct),
            activity = activity.Select(a => new { at = a.At, method = a.Method, path = a.Path, statusCode = a.StatusCode, from = a.From }),
        });
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
        => await _grants.RevokeAsync(id, ct) ? NoContent() : NotFound(new { error = "No live grant has this id." });

    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAll(CancellationToken ct)
        => Ok(new { revoked = await _grants.RevokeAllAsync(ct) });

    private static object View(ExternalAgentGrant grant)
    {
        var now = DateTime.UtcNow;
        return new
        {
            id = grant.Id,
            name = grant.Name,
            scope = grant.Scope,
            createdBy = grant.CreatedBy,
            createdAt = grant.CreatedAt,
            expiresAt = grant.ExpiresAt,
            lastUsedAt = grant.LastUsedAt,
            lastUsedFrom = grant.LastUsedFrom,
            revokedAt = grant.RevokedAt,
            status = IExternalAgentGrantService.StatusOf(grant, now).ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// What the grant proposed that no human has decided yet, of every kind. A grant that ended leaves its pending
    /// proposals here and decidable: nothing about deciding one depends on the grant being live.
    /// </summary>
    private async Task<IReadOnlyList<object>> PendingProposalsAsync(Guid grantId, CancellationToken ct)
    {
        var proposals = new List<(DateTime CreatedAt, object Item)>();

        foreach (var p in await _workItems.QueryEditProposalsAsync(
                     new RemoteEditProposalQuery { CreatedByExternalGrantId = grantId, Status = RemoteEditProposalStatus.Pending }, ct))
            proposals.Add((p.CreatedAt, new
            {
                kind = "workItemEdit",
                id = p.Id,
                target = p.WorkItemId,
                summary = p.Proposed.Title ?? $"Edit of work item {p.WorkItemId}",
                rationale = p.Rationale,
                createdAt = p.CreatedAt,
            }));

        foreach (var e in await _globalProposals.ListAsync(
                     new GlobalVariableProposalQuery(Status: GlobalVariableProposalStatus.Pending, CreatedByExternalGrantId: grantId), ct))
            proposals.Add((e.Proposal.CreatedAt, new
            {
                kind = "globalVariable",
                id = e.Proposal.Id,
                target = e.Proposal.Name,
                summary = $"New value for the global variable {e.Proposal.Name}",
                rationale = e.Proposal.Rationale,
                createdAt = e.Proposal.CreatedAt,
            }));

        foreach (var e in await _templateProposals.ListAsync(
                     new LoopTemplateProposalQuery(Status: LoopTemplateProposalStatus.Pending, CreatedByExternalGrantId: grantId), ct))
            proposals.Add((e.Proposal.CreatedAt, new
            {
                kind = "loopTemplate",
                id = e.Proposal.Id,
                target = e.Proposal.LoopTemplateId,
                summary = $"Change to the loop template {e.Template?.Name ?? e.Proposal.LoopTemplateId.ToString()}",
                rationale = e.Proposal.Rationale,
                createdAt = e.Proposal.CreatedAt,
            }));

        foreach (var p in await _db.HumanInputProposals.AsNoTracking()
                     .Where(p => p.CreatedByExternalGrantId == grantId && p.Status == HumanInputProposalStatus.Pending)
                     .ToListAsync(ct))
            proposals.Add((p.CreatedAt, new
            {
                kind = "humanInput",
                id = p.Id,
                target = p.WorkItemId,
                summary = $"Answer {p.Output} for work item {p.WorkItemId}",
                rationale = p.Rationale,
                createdAt = p.CreatedAt,
            }));

        return proposals.OrderByDescending(p => p.CreatedAt).Select(p => p.Item).ToList();
    }
}
