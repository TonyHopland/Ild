using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AiProvidersController : ControllerBase
{
    private readonly IAIProviderService _aiProviderService;

    public AiProvidersController(IAIProviderService aiProviderService)
    {
        _aiProviderService = aiProviderService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var providers = await _aiProviderService.GetAvailableProvidersAsync();
        return Ok(providers);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        throw new NotImplementedException();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AiProviderDto request)
    {
        throw new NotImplementedException();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AiProviderDto request)
    {
        throw new NotImplementedException();
    }
}
