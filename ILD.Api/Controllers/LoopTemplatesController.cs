using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class LoopTemplatesController : ControllerBase
{
    private readonly ILoopTemplateManager _loopTemplateManager;

    public LoopTemplatesController(ILoopTemplateManager loopTemplateManager)
    {
        _loopTemplateManager = loopTemplateManager;
    }

    private async Task<object> ToFlatAsync(LoopTemplate t)
    {
        var graph = await _loopTemplateManager.GetLatestGraphAsync(t.Id);
        var versions = await _loopTemplateManager.GetVersionsAsync(t.Id);
        var versionNumber = versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault()?.VersionNumber ?? 0;
        return new
        {
            id = t.Id,
            name = t.Name,
            description = t.Description ?? string.Empty,
            isDefault = t.IsDefault,
            recoveryPolicy = t.RecoveryPolicy.ToString(),
            maxNodeExecutions = t.MaxNodeExecutions,
            maxWallClockHours = t.MaxWallClockHours,
            version = versionNumber,
            nodes = graph?.Nodes ?? new List<LoopNodeDto>(),
            edges = graph?.Edges ?? new List<LoopNodeEdgeDto>(),
            createdAt = t.CreatedAt,
            updatedAt = t.UpdatedAt,
            isArchived = t.IsArchived,
        };
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] bool includeArchived = false)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var templates = await _loopTemplateManager.GetAllLoopTemplatesAsync(skip, take, includeArchived);
        var results = new List<object>();
        foreach (var t in templates)
            results.Add(await ToFlatAsync(t));
        return Ok(results);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var template = await _loopTemplateManager.GetLoopTemplateAsync(guid);
        if (template == null)
            return NotFound();

        return Ok(await ToFlatAsync(template));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LoopTemplateCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var graph = new LoopTemplateGraph(Guid.Empty, request.Nodes, request.Edges);
            var id = await _loopTemplateManager.CreateLoopTemplateAsync(
                request.Name,
                request.Description,
                graph,
                request.RecoveryPolicy,
                request.MaxNodeExecutions,
                request.MaxWallClockHours);
            return CreatedAtAction(nameof(GetById), new { id }, new { id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] LoopTemplateCreateRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        try
        {
            var graph = new LoopTemplateGraph(Guid.Empty, request.Nodes, request.Edges);
            var newId = await _loopTemplateManager.UpdateLoopTemplateAsync(
                guid,
                request.Name,
                request.Description,
                graph,
                request.RecoveryPolicy,
                request.MaxNodeExecutions,
                request.MaxWallClockHours);
            return Ok(new { id = newId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/clone")]
    public async Task<IActionResult> Clone(string id, [FromQuery] string? newName = null)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var cloned = await _loopTemplateManager.CloneLoopTemplateAsync(guid, newName ?? "Clone");
        return Ok(new { id = cloned });
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> GetVersions(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var versions = await _loopTemplateManager.GetVersionsAsync(guid);
        return Ok(versions);
    }

    [HttpGet("{id}/versions/{versionNumber}")]
    public async Task<IActionResult> GetVersionGraph(string id, int versionNumber)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var graph = await _loopTemplateManager.GetVersionGraphAsync(guid, versionNumber);
        if (graph == null)
            return NotFound(new { error = $"Version {versionNumber} not found" });

        return Ok(new
        {
            nodes = graph.Nodes,
            edges = graph.Edges,
        });
    }

    [HttpPost("validate")]
    public async Task<IActionResult> ValidateGraph([FromBody] LoopTemplateGraph graph)
    {
        var (valid, errors) = await _loopTemplateManager.ValidateGraphAsync(graph);
        return Ok(new { valid, errors });
    }

    [HttpPost("{id}/archive")]
    public async Task<IActionResult> Archive(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        await _loopTemplateManager.ArchiveLoopTemplateAsync(guid);
        return NoContent();
    }

    [HttpPost("{id}/unarchive")]
    public async Task<IActionResult> Unarchive(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        await _loopTemplateManager.UnarchiveLoopTemplateAsync(guid);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        await _loopTemplateManager.DeleteLoopTemplateAsync(guid);
        return NoContent();
    }
}
