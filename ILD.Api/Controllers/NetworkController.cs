using System.ComponentModel.DataAnnotations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// The egress filter's lists and log (ADR-0019). The mode toggle is an app
/// setting (<c>network.mode</c>, via <see cref="SettingsController"/>); this is
/// everything that does not fit in a setting value. Every list edit invalidates
/// the proxy's cached policy, so the agent's next connection is judged by it.
/// </summary>
[ApiController]
[Route("api/v1/network")]
public class NetworkController : ControllerBase
{
    private const int DefaultLogTake = 200;
    private const int MaxLogTake = 1000;

    private readonly INetworkPolicyStore _store;
    private readonly IProviderStore _providers;
    private readonly IEgressPolicy _policy;
    private readonly INetworkNotifier _notifier;
    private readonly NetworkEnforcementStatus _enforcement;

    public NetworkController(
        INetworkPolicyStore store,
        IProviderStore providers,
        IEgressPolicy policy,
        INetworkNotifier notifier,
        NetworkEnforcementStatus enforcement)
    {
        _store = store;
        _providers = providers;
        _policy = policy;
        _notifier = notifier;
        _enforcement = enforcement;
    }

    public sealed class AddEntryRequest
    {
        [Required]
        public string Host { get; set; } = string.Empty;

        [Required]
        public NetworkListKind ListKind { get; set; }

        public Guid? AiProviderId { get; set; }
    }

    public sealed class AddFromLogRequest
    {
        /// <summary>Scope the new entry to the log entry's provider instead of making it global.</summary>
        public bool ScopeToProvider { get; set; }
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
        => Ok(new
        {
            enforcement = _enforcement.Enforcement,
            reason = _enforcement.Reason,
            proxyEnabled = _enforcement.ProxyEnabled,
            proxyPort = _enforcement.ProxyPort,
        });

    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken ct)
        => Ok((await _store.GetEntriesAsync(ct)).Select(View));

    [HttpPost("entries")]
    public async Task<IActionResult> AddEntry([FromBody] AddEntryRequest request, CancellationToken ct)
    {
        if (!EgressRules.TryNormalizePattern(request.Host, out var host, out var error))
            return BadRequest(new { error });
        if (request.AiProviderId is { } providerId && await _providers.GetAiProviderByIdAsync(providerId) is null)
            return BadRequest(new { error = "Unknown AI provider" });

        var entry = await AddAsync(host, request.ListKind, request.AiProviderId, ct);
        return CreatedAtAction(nameof(GetEntries), View(entry));
    }

    [HttpDelete("entries/{id:guid}")]
    public async Task<IActionResult> DeleteEntry(Guid id, CancellationToken ct)
    {
        if (!await _store.DeleteEntryAsync(id, ct)) return NotFound();
        await PolicyChangedAsync();
        return NoContent();
    }

    [HttpGet("log")]
    public async Task<IActionResult> GetLog([FromQuery] int take = DefaultLogTake, CancellationToken ct = default)
        => Ok((await _store.GetLogAsync(Math.Clamp(take, 1, MaxLogTake), ct)).Select(View));

    [HttpDelete("log")]
    public async Task<IActionResult> ClearLog(CancellationToken ct)
    {
        var removed = await _store.ClearLogAsync(ct);
        await _notifier.LogClearedAsync();
        return Ok(new { removed });
    }

    [HttpPost("log/{id:guid}/whitelist")]
    public Task<IActionResult> WhitelistFromLog(Guid id, [FromBody] AddFromLogRequest? request, CancellationToken ct)
        => AddFromLogAsync(id, NetworkListKind.Whitelist, request?.ScopeToProvider ?? false, ct);

    [HttpPost("log/{id:guid}/blacklist")]
    public Task<IActionResult> BlacklistFromLog(Guid id, [FromBody] AddFromLogRequest? request, CancellationToken ct)
        => AddFromLogAsync(id, NetworkListKind.Blacklist, request?.ScopeToProvider ?? false, ct);

    private async Task<IActionResult> AddFromLogAsync(Guid logId, NetworkListKind kind, bool scopeToProvider, CancellationToken ct)
    {
        var logged = await _store.GetLogEntryAsync(logId, ct);
        if (logged is null) return NotFound();
        if (!EgressRules.TryNormalizePattern(logged.Host, out var host, out var error))
            return BadRequest(new { error });

        var providerId = scopeToProvider ? logged.AiProviderId : null;
        if (providerId is { } id && await _providers.GetAiProviderByIdAsync(id) is null)
            providerId = null;

        var existing = (await _store.GetEntriesAsync(ct))
            .FirstOrDefault(e => e.ListKind == kind && e.Host == host && e.AiProviderId == providerId);
        if (existing is not null) return Ok(View(existing));

        var entry = await AddAsync(host, kind, providerId, ct);
        return CreatedAtAction(nameof(GetEntries), View(entry));
    }

    private async Task<NetworkPolicyEntry> AddAsync(string host, NetworkListKind kind, Guid? providerId, CancellationToken ct)
    {
        var entry = new NetworkPolicyEntry
        {
            Id = Guid.NewGuid(),
            Host = host,
            ListKind = kind,
            AiProviderId = providerId,
            CreatedAt = DateTime.UtcNow,
        };
        await _store.AddEntryAsync(entry, ct);
        await PolicyChangedAsync();
        return entry;
    }

    private async Task PolicyChangedAsync()
    {
        _policy.Invalidate();
        await _notifier.PolicyChangedAsync();
    }

    private static object View(NetworkPolicyEntry e)
        => new { id = e.Id, host = e.Host, listKind = e.ListKind, aiProviderId = e.AiProviderId, createdAt = e.CreatedAt };

    private static object View(NetworkLogEntry l)
        => new { id = l.Id, host = l.Host, port = l.Port, timestamp = l.Timestamp, decision = l.Decision, aiProviderId = l.AiProviderId };
}
