using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RepositoriesController : ControllerBase
{
    private readonly IRepositoryManager _repositoryManager;

    public RepositoriesController(IRepositoryManager repositoryManager)
    {
        _repositoryManager = repositoryManager;
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
    public async Task<IActionResult> Create([FromBody] RepositoryDto request)
    {
        throw new NotImplementedException();
    }
}
