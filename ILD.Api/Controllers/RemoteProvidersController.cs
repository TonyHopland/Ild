using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RemoteProvidersController : ControllerBase
{
    private readonly IRemoteProvider _remoteProvider;

    public RemoteProvidersController(IRemoteProvider remoteProvider)
    {
        _remoteProvider = remoteProvider;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        throw new NotImplementedException();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        throw new NotImplementedException();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RemoteProviderDto request)
    {
        throw new NotImplementedException();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] RemoteProviderDto request)
    {
        throw new NotImplementedException();
    }
}
