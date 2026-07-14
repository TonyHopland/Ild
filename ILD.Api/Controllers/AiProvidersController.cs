using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Api.Services;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
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
    private readonly IProviderStore _providerStore;
    private readonly InteractiveProviderSessionService _interactiveSessions;
    private readonly IManagedAgentProvisioner _agentProvisioner;

    public AiProvidersController(
        IAIProviderService aiProviderService,
        IAgentAdapterRegistry adapterRegistry,
        AppDbContext db,
        IProviderStore providerStore,
        InteractiveProviderSessionService interactiveSessions,
        IManagedAgentProvisioner agentProvisioner)
    {
        _aiProviderService = aiProviderService;
        _supportedProviderTypes = adapterRegistry.GetAllSupportedProviderTypes()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _db = db;
        _providerStore = providerStore;
        _interactiveSessions = interactiveSessions;
        _agentProvisioner = agentProvisioner;
    }

    /// <summary>
    /// Provider types whose authentication is handled by the CLI itself
    /// (e.g. <c>claude-code</c> uses the Max-subscription session stored in
    /// <c>~/.claude</c>, and <c>copilot</c> uses the GitHub Copilot session
    /// stored in <c>~/.copilot</c>). For these we do not require BaseUrl, ApiKey
    /// or Model on the AiProvider record.
    /// </summary>
    private static readonly HashSet<string> CliAuthProviderTypes =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code", "copilot" };

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

    /// <summary>
    /// Fold the UI-managed Custom MCP servers value into a provider's config blob,
    /// preserving every other key (including secrets the UI never sees, such as an
    /// embedded <c>apiKey</c>). A null <paramref name="customMcpServersJson"/> means
    /// the caller isn't managing the field, so the blob is returned unchanged; a
    /// blank value clears the key. The stored key is camelCase to match the shape
    /// <see cref="AiProviderConfig"/> reads.
    /// </summary>
    private static string? ApplyCustomMcpServers(string? configJson, string? customMcpServersJson)
    {
        if (customMcpServersJson is null) return configJson;

        JsonObject obj;
        try
        {
            obj = (string.IsNullOrWhiteSpace(configJson)
                ? null
                : JsonNode.Parse(configJson) as JsonObject) ?? new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }

        if (string.IsNullOrWhiteSpace(customMcpServersJson))
            obj.Remove("customMcpServersJson");
        else
            obj["customMcpServersJson"] = customMcpServersJson;

        return obj.Count == 0 ? null : obj.ToJsonString();
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
        // The config blob is never returned whole — it can embed a secret (e.g.
        // a Pi provider's apiKey, read by PiAdapter). Surface only the non-secret,
        // user-editable Custom MCP servers value so the AI Providers form can seed
        // and round-trip it without leaking the rest of the blob.
        customMcpServersJson = AiProviderConfig.Parse(p.Config).CustomMcpServersJson,
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
            Config = ApplyCustomMcpServers(request.Config, request.CustomMcpServersJson),
            CreatedAt = DateTime.UtcNow,
        };
        await _providerStore.CreateAiProviderAsync(p);
        // Agents aren't baked into the image; if this provider uses a managed
        // agent that isn't installed yet, install it in the background so the
        // first run doesn't fail on a missing CLI.
        _agentProvisioner.EnsureInstalledForProviderType(p.Type);
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

    [HttpPost("{id}/set-default")]
    public async Task<IActionResult> SetDefault(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _providerStore.GetAiProviderByIdAsync(guid);
        if (p == null) return NotFound();
        if (p.IsDefault) return Ok(ToResponse(p));
        p.IsDefault = true;
        p.UpdatedAt = DateTime.UtcNow;
        await _providerStore.UpdateAiProviderAsync(p);
        return Ok(ToResponse(p));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AiProviderDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _providerStore.GetAiProviderByIdAsync(guid);
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
        // Advanced callers may replace the whole blob via Config; otherwise keep the
        // stored blob as the base so keys the UI never sees (e.g. a Pi provider's
        // embedded apiKey) survive an edit. The Custom MCP servers value is then
        // folded in on top.
        p.Config = ApplyCustomMcpServers(request.Config ?? p.Config, request.CustomMcpServersJson);
        p.UpdatedAt = DateTime.UtcNow;
        await _providerStore.UpdateAiProviderAsync(p);
        // If the type was changed to a managed agent, make sure it is installed.
        _agentProvisioner.EnsureInstalledForProviderType(p.Type);
        return Ok(ToResponse(p));
    }
}
