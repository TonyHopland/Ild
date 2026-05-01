using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
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

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var templates = await _loopTemplateManager.GetAllLoopTemplatesAsync(skip, take);
        return Ok(templates);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var template = await _loopTemplateManager.GetLoopTemplateAsync(guid);
        if (template == null)
            return NotFound();

        return Ok(template);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LoopTemplateCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var graph = new LoopTemplateGraph(Guid.Empty, request.Nodes, request.Edges);
            var id = await _loopTemplateManager.CreateLoopTemplateAsync(request.Name, request.Description, graph);
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
            var newId = await _loopTemplateManager.UpdateLoopTemplateAsync(guid, request.Name, request.Description, graph);
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

    [HttpPost("validate")]
    public async Task<IActionResult> ValidateGraph([FromBody] LoopTemplateGraph graph)
    {
        var valid = await _loopTemplateManager.ValidateGraphAsync(graph);
        return Ok(new { valid });
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
