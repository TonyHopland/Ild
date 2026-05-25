using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AiProvidersController : ControllerBase
{
    private readonly IAIProviderService _aiProviderService;
    private readonly HashSet<string> _supportedProviderTypes;
    private readonly AppDbContext _db;
    private readonly InteractiveProviderSessionService _interactiveSessions;

    public AiProvidersController(
        IAIProviderService aiProviderService,
        IAgentAdapterRegistry adapterRegistry,
        AppDbContext db,
        InteractiveProviderSessionService interactiveSessions)
    {
        _aiProviderService = aiProviderService;
        _supportedProviderTypes = adapterRegistry.GetAllSupportedProviderTypes()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _db = db;
        _interactiveSessions = interactiveSessions;
    }

    /// <summary>
    /// Provider types whose authentication is handled by the CLI itself
    /// (e.g. <c>claude-code</c> uses the Max-subscription session stored in
    /// <c>~/.claude</c>). For these we do not require BaseUrl, ApiKey or
    /// Model on the AiProvider record.
    /// </summary>
    private static readonly HashSet<string> CliAuthProviderTypes =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code" };

    private static string? ValidateConnectionFields(AiProviderDto request)
    {
        if (CliAuthProviderTypes.Contains(request.Type))
            return null;

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            return "BaseUrl is required for this provider type.";
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out _))
            return "BaseUrl must be an absolute URL.";
        if (string.IsNullOrWhiteSpace(request.Model))
            return "Model is required for this provider type.";

        return null;
    }

    private static object ToResponse(AiProvider p) => new
    {
        id = p.Id,
        name = p.Name,
        type = p.Type,
        baseUrl = p.BaseUrl,
        model = p.Model,
        isDefault = p.IsDefault,
        parallelism = p.Parallelism,
        apiKey = string.IsNullOrEmpty(p.ApiKey) ? null : "***",
        hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
        hasConfig = !string.IsNullOrEmpty(p.Config),
        supportedTools = AiToolCatalog.GetSupportedToolsForProviderType(p.Type),
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var items = await _db.AiProviders.AsNoTracking().OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync();
        return Ok(items.Select(ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.AiProviders.FindAsync(guid);
        return p == null ? NotFound() : Ok(ToResponse(p));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AiProviderDto request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (!_supportedProviderTypes.Contains(request.Type))
            return BadRequest(new { error = $"Unsupported AI provider type '{request.Type}'." });
        if (ValidateConnectionFields(request) is { } validationError)
            return BadRequest(new { error = validationError });

        var p = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Type = request.Type,
            BaseUrl = request.BaseUrl,
            Model = request.Model,
            ApiKey = string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey,
            IsDefault = request.IsDefault,
            Parallelism = request.Parallelism,
            Config = request.Config,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiProviders.Add(p);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = p.Id }, ToResponse(p));
    }

    [HttpGet("{id}/interactive")]
    public async Task<IActionResult> OpenInteractiveSession(string id, [FromQuery] int cols = 120, [FromQuery] int rows = 30)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "Expected WebSocket upgrade request." });
        if (!Guid.TryParse(id, out var guid))
            return BadRequest();

        var provider = await _db.AiProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == guid);
        if (provider is null) return NotFound();

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await _interactiveSessions.RunAsync(socket, provider, cols, rows, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AiProviderDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.AiProviders.FindAsync(guid);
        if (p == null) return NotFound();
        if (!_supportedProviderTypes.Contains(request.Type))
            return BadRequest(new { error = $"Unsupported AI provider type '{request.Type}'." });
        if (ValidateConnectionFields(request) is { } validationError)
            return BadRequest(new { error = validationError });
        p.Name = request.Name;
        p.Type = request.Type;
        p.BaseUrl = request.BaseUrl;
        p.Model = request.Model;
        if (!string.IsNullOrEmpty(request.ApiKey)) p.ApiKey = request.ApiKey;
        p.IsDefault = request.IsDefault;
        p.Parallelism = request.Parallelism;
        p.Config = request.Config;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(p));
    }
}
