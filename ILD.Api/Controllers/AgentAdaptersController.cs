using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AgentAdaptersController : ControllerBase
{
    private readonly IAgentAdapterRegistry _registry;

    public AgentAdaptersController(IAgentAdapterRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet]
    public IActionResult GetSupportedProviderTypes()
    {
        var types = _registry.GetAllSupportedProviderTypes();
        return Ok(types);
    }

    [HttpGet("{providerType}/config-schema")]
    public IActionResult GetConfigSchema(string providerType)
    {
        var fakeProvider = new AiProvider { Type = providerType };
        try
        {
            var factory = _registry.ResolveForProvider(fakeProvider);
            var adapter = factory();
            return Ok(adapter.ConfigSchema);
        }
        catch (InvalidOperationException)
        {
            return NotFound($"No adapter registered for provider type '{providerType}'");
        }
    }
}
