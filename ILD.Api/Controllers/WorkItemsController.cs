using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class WorkItemsController : ControllerBase
{
    private readonly IWorkItemManager _workItemManager;

    public WorkItemsController(IWorkItemManager workItemManager)
    {
        _workItemManager = workItemManager;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        throw new NotImplementedException();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var workItem = await _workItemManager.GetWorkItemAsync(guid);
        if (workItem == null)
            return NotFound();

        return Ok(workItem);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] WorkItemCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var loopTemplateId = Guid.TryParse(request.LoopTemplateId, out var ltGuid) ? (Guid?)ltGuid : null;
        var repositoryId = Guid.TryParse(request.RepositoryId, out var rGuid) ? (Guid?)rGuid : null;
        var id = await _workItemManager.CreateWorkItemAsync(
            request.Title, request.Description,
            loopTemplateId, repositoryId);

        return CreatedAtAction(nameof(GetById), new { id }, new { id });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WorkItemCreateRequest request)
    {
        throw new NotImplementedException();
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var success = await _workItemManager.TransitionToRunningAsync(guid);
        return success ? Ok() : NotFound();
    }

    [HttpPost("{id}/transition")]
    public async Task<IActionResult> Transition(string id, [FromBody] WorkItemTransitionRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        throw new NotImplementedException();
    }

    [HttpGet("{id}/dependencies")]
    public async Task<IActionResult> GetDependencies(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var dependencies = await _workItemManager.GetDependenciesAsync(guid);
        return Ok(dependencies);
    }

    [HttpPost("{id}/dependencies")]
    public async Task<IActionResult> AddDependency(string id, [FromBody] AddDependencyRequest request)
    {
        if (!Guid.TryParse(id, out var workItemId) || !Guid.TryParse(request.DependencyId, out var depId))
            return BadRequest(new { error = "Invalid GUID" });

        var success = await _workItemManager.AddDependencyAsync(workItemId, depId);
        return success ? Ok() : BadRequest();
    }

    [HttpGet("{id}/runs")]
    public async Task<IActionResult> GetRuns(string id)
    {
        throw new NotImplementedException();
    }
}

public class AddDependencyRequest
{
    public string DependencyId { get; set; } = string.Empty;
}
