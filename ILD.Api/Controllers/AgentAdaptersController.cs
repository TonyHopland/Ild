using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
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

    /// <summary>
    /// Every registered provider type with what its adapter declares about a
    /// model selector and how its CLI signs in, so the AI Providers form can offer
    /// the Model field — and mark it required or optional — and the login note for
    /// a type no provider has been created for yet.
    /// </summary>
    [HttpGet]
    public IActionResult GetSupportedProviderTypes()
    {
        var adapters = _registry.GetAllSupportedProviderTypes()
            .Select(type =>
            {
                var traits = _registry.GetTraits(type);
                return new AgentAdapterDescriptor(type, _registry.GetModelSupport(type), traits.DisplayName, traits.CliAuth);
            })
            .ToArray();
        return Ok(adapters);
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
