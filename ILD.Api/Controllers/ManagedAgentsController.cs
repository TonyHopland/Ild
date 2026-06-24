using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

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
}
