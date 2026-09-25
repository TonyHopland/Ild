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
    private readonly IAgentAdapterRegistry _adapterRegistry;
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
        _adapterRegistry = adapterRegistry;
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
    /// stored in <c>~/.copilot</c>). For these we do not require BaseUrl or
    /// ApiKey on the AiProvider record. Whether a Model is required is a
    /// separate question, answered by the adapter's declared
    /// <see cref="AdapterModelSupport"/>.
    /// </summary>
    private static readonly HashSet<string> CliAuthProviderTypes =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code", "copilot" };

    private string? ValidateConnectionFields(AiProviderDto request)
    {
        if (!CliAuthProviderTypes.Contains(request.Type))
        {
            if (string.IsNullOrWhiteSpace(request.BaseUrl))
                return "BaseUrl is required for this provider type.";
            if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out _))
                return "BaseUrl must be an absolute URL.";
        }

        if (_adapterRegistry.GetModelSupport(request.Type) == AdapterModelSupport.Required
            && string.IsNullOrWhiteSpace(request.Model))
            return "Model is required for this provider type.";

        return null;
    }

    /// <summary>
    /// Fold the UI-managed Custom MCP servers value into a provider's config blob,
    /// preserving every other key of a well-formed JSON object (including secrets
    /// the UI never sees, such as an embedded <c>apiKey</c>). A null
    /// <paramref name="customMcpServersJson"/> means the caller isn't managing the
    /// field, so the blob is returned unchanged; a blank value clears the key. If
    /// the existing blob is malformed or not a JSON object it can't be merged into,
    /// so it fails open to a fresh object holding just this key (mirroring
    /// <see cref="AiProviderConfig.Parse"/>) — the only case where other keys are
    /// not carried over. The stored key is camelCase to match the shape
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

    /// <summary>
    /// Trims the requested tags, drops blank ones and collapses case-duplicates
    /// to their first spelling. Null stays null: the caller isn't managing tags.
    /// </summary>
    private static (List<string>? Tags, string? Error) NormalizeTags(List<string>? requested)
    {
        if (requested is null) return (null, null);
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in requested.Select(t => t?.Trim()))
        {
            if (string.IsNullOrEmpty(tag) || !seen.Add(AiProviderTag.Normalize(tag))) continue;
            if (AiProviderTag.Problem(tag) is { } problem)
                return (null, $"Tag '{tag}' {problem}.");
            tags.Add(tag);
        }
        if (tags.Count > AiProviderTag.MaxPerProvider)
            return (null, $"A provider can have at most {AiProviderTag.MaxPerProvider} tags; got {tags.Count}.");
        return (tags, null);
    }

    private sealed record TagHolder(Guid ProviderId, string ProviderName);

    /// <summary>Who holds each of <paramref name="tags"/> now, keyed by normalised name.</summary>
    private async Task<Dictionary<string, TagHolder>> TagHoldersAsync(IReadOnlyList<string> tags)
    {
        var normalized = tags.Select(AiProviderTag.Normalize).ToList();
        return await _db.AiProviderTags.AsNoTracking()
            .Where(t => normalized.Contains(t.NormalizedName))
            .Select(t => new { t.NormalizedName, t.AiProviderId, t.AiProvider!.Name })
            .ToDictionaryAsync(t => t.NormalizedName, t => new TagHolder(t.AiProviderId, t.Name));
    }

    /// <summary>
    /// Runs <paramref name="save"/> and returns the conflict message when the
    /// database refused it because a concurrent save gave one of
    /// <paramref name="tags"/> to another provider (the unique tag index), or
    /// null when it succeeded. Any other failure propagates. The rollback puts a
    /// tag this save was moving back on its old holder, so only a holder that
    /// changed since before the save means another save took it.
    /// </summary>
    private async Task<string?> SaveReportingTagConflictAsync(Guid providerId, IReadOnlyList<string>? tags, Func<Task> save)
    {
        if (tags is null)
        {
            await save();
            return null;
        }
        var holdersBefore = await TagHoldersAsync(tags);
        try
        {
            await save();
            return null;
        }
        catch (DbUpdateException)
        {
            if (await TagTakenConcurrentlyAsync(providerId, tags, holdersBefore) is not { } conflict) throw;
            return conflict;
        }
    }

    private async Task<string?> TagTakenConcurrentlyAsync(
        Guid providerId, IReadOnlyList<string> tags, Dictionary<string, TagHolder> holdersBefore)
    {
        var holdersAfter = await TagHoldersAsync(tags);
        foreach (var tag in tags)
        {
            var key = AiProviderTag.Normalize(tag);
            if (holdersAfter.GetValueOrDefault(key) is { } holder && holder.ProviderId != providerId
                && holder.ProviderId != holdersBefore.GetValueOrDefault(key)?.ProviderId)
                return $"Tag '{tag}' was saved on provider '{holder.ProviderName}' at the same time. Reload and try again.";
        }
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
        // The config blob is never returned whole — it can embed a secret (e.g.
        // a Pi provider's apiKey, read by PiAdapter). Surface only the non-secret,
        // user-editable Custom MCP servers value so the AI Providers form can seed
        // and round-trip it without leaking the rest of the blob.
        customMcpServersJson = AiProviderConfig.Parse(p.Config).CustomMcpServersJson,
        tags = p.Tags.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
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
        var items = await _db.AiProviders.AsNoTracking().Include(p => p.Tags).OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync();
        return Ok(items.Select(ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _providerStore.GetAiProviderByIdAsync(guid);
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
        var (tags, tagsError) = NormalizeTags(request.Tags);
        if (tagsError is not null)
            return BadRequest(new { error = tagsError });

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
        if (await SaveReportingTagConflictAsync(p.Id, tags, () => _providerStore.CreateAiProviderAsync(p, tags)) is { } conflict)
            return Conflict(new { error = conflict });
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
        var (tags, tagsError) = NormalizeTags(request.Tags);
        if (tagsError is not null)
            return BadRequest(new { error = tagsError });
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
        if (await SaveReportingTagConflictAsync(p.Id, tags, () => _providerStore.UpdateAiProviderAsync(p, tags)) is { } conflict)
            return Conflict(new { error = conflict });
        // If the type was changed to a managed agent, make sure it is installed.
        _agentProvisioner.EnsureInstalledForProviderType(p.Type);
        return Ok(ToResponse(p));
    }
}
