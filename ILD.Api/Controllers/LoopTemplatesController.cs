using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
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
    public async Task<IActionResult> GetAll()
    {
        var templates = await _loopTemplateManager.GetAllLoopTemplatesAsync();
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

        throw new NotImplementedException();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] LoopTemplateCreateRequest request)
    {
        throw new NotImplementedException();
    }

    [HttpPost("{id}/clone")]
    public async Task<IActionResult> Clone(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var cloned = await _loopTemplateManager.CloneLoopTemplateAsync(guid, "Clone");
        return Ok(cloned);
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> GetVersions(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        throw new NotImplementedException();
    }

    [HttpGet("{id}/validate")]
    public async Task<IActionResult> Validate(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var valid = true;
        return Ok(new { valid });
    }
}
