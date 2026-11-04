using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ILD.Api.Controllers;

/// <summary>
/// Version state and user-triggered updates for the managed coding agents
/// (Pi, OpenCode, Claude Code). Backs the AI Provider page's "Update {old} → {new}" button.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class ManagedAgentsController : ControllerBase
{
    private readonly IManagedAgentService _service;

    public ManagedAgentsController(IManagedAgentService service)
    {
        _service = service;
    }

    /// <summary>Current + latest version for every managed agent.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var statuses = await _service.GetStatusesAsync(ct);
        return Ok(statuses);
    }

    /// <summary>
    /// Install the latest version of an agent onto <c>/data</c> and make it
    /// active, then return the refreshed version state.
    /// </summary>
    [HttpPost("{key}/update")]
    public async Task<IActionResult> Update(string key, CancellationToken ct)
    {
        if (ManagedAgentCatalog.Find(key) is null)
            return NotFound(new { error = $"Unknown agent '{key}'." });

        try
        {
            var status = await _service.UpdateAsync(key, ct);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            // Install failed (npm error, registry unreachable, ...). The previous
            // version is left intact; surface the reason so the UI can show it.
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Switch an agent back to the version it had before its last update, at once and even while
    /// it is in use, then return the refreshed version state. A body naming the version the caller
    /// was shown (<c>previousVersion</c>) makes it refuse, with 409, when that is no longer the one.
    /// </summary>
    [HttpPost("{key}/rollback")]
    public async Task<IActionResult> Rollback(
        string key,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RollbackRequest? request,
        CancellationToken ct)
    {
        if (ManagedAgentCatalog.Find(key) is null)
            return NotFound(new { error = $"Unknown agent '{key}'." });

        try
        {
            return Ok(await _service.RollbackAsync(key, request?.PreviousVersion, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    public sealed record RollbackRequest(string? PreviousVersion);
}
