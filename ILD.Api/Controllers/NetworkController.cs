using System.ComponentModel.DataAnnotations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// The egress filter's lists and log (ADR-0019), and the declared forwards
/// (ADR-0020). The mode toggle is an app setting (<c>network.mode</c>, via
/// <see cref="SettingsController"/>); this is everything that does not fit in a
/// setting value. Every edit here invalidates the proxy's cached policy, so the
/// agent's next connection — and the forwarder's listener set — is judged by it.
/// </summary>
[ApiController]
[Route("api/v1/network")]
public class NetworkController : ControllerBase
{
    private const int DefaultLogTake = 200;
    private const int MaxLogTake = 1000;
    private const int MaxForwardNameLength = 128;

    private readonly INetworkPolicyStore _store;
    private readonly INetworkForwardStore _forwards;
    private readonly IProviderStore _providers;
    private readonly IEgressPolicy _policy;
    private readonly INetworkNotifier _notifier;
    private readonly NetworkEnforcementStatus _enforcement;
    private readonly IEgressForwarderState _forwarder;

    public NetworkController(
        INetworkPolicyStore store,
        INetworkForwardStore forwards,
        IProviderStore providers,
        IEgressPolicy policy,
        INetworkNotifier notifier,
        NetworkEnforcementStatus enforcement,
        IEgressForwarderState forwarder)
    {
        _store = store;
        _forwards = forwards;
        _providers = providers;
        _policy = policy;
        _notifier = notifier;
        _enforcement = enforcement;
        _forwarder = forwarder;
    }

    public sealed class AddEntryRequest
    {
        [Required]
        public string Host { get; set; } = string.Empty;

        [Required]
        public NetworkListKind ListKind { get; set; }

        public Guid? AiProviderId { get; set; }
    }

    public sealed class AddForwardRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        /// <summary>One concrete host or IP literal — not a list pattern.</summary>
        [Required]
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; }

        public int LocalPort { get; set; }
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
        if (!Enum.IsDefined(request.ListKind))
            return BadRequest(new { error = "listKind must be 'Whitelist' or 'Blacklist'" });
        if (!EgressRules.TryNormalizePattern(request.Host, out var host, out var error))
            return BadRequest(new { error });
        if (request.AiProviderId is { } providerId && await _providers.GetAiProviderByIdAsync(providerId) is null)
            return BadRequest(new { error = "Unknown AI provider" });

        var (entry, created) = await AddOrFindAsync(host, request.ListKind, request.AiProviderId, ct);
        return created ? CreatedAtAction(nameof(GetEntries), View(entry)) : Ok(View(entry));
    }

    [HttpDelete("entries/{id:guid}")]
    public async Task<IActionResult> DeleteEntry(Guid id, CancellationToken ct)
    {
        if (!await _store.DeleteEntryAsync(id, ct)) return NotFound();
        await PolicyChangedAsync();
        return NoContent();
    }

    [HttpGet("forwards")]
    public async Task<IActionResult> GetForwards(CancellationToken ct)
    {
        var policy = await _policy.GetAsync(ct);
        return Ok((await _forwards.GetForwardsAsync(ct)).Select(f => View(f, policy)));
    }

    [HttpPost("forwards")]
    public async Task<IActionResult> AddForward([FromBody] AddForwardRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { error = "Give the forward a name, e.g. postgres" });
        if (name.Length > MaxForwardNameLength)
            return BadRequest(new { error = $"Names are at most {MaxForwardNameLength} characters" });
        if (!EgressRules.TryNormalizeForwardHost(request.Host, out var host, out var error))
            return BadRequest(new { error });
        if (request.Port is < 1 or > 65535)
            return BadRequest(new { error = "The destination port must be between 1 and 65535" });
        if (request.LocalPort is < 1 or > 65535)
            return BadRequest(new { error = "The local port must be between 1 and 65535" });
        if (await _forwards.FindByLocalPortAsync(request.LocalPort, ct) is { } taken)
            return BadRequest(new { error = $"Local port {request.LocalPort} already forwards to {taken.Host}:{taken.Port}" });

        var forward = new NetworkForwardEntry
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = host,
            Port = request.Port,
            LocalPort = request.LocalPort,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            await _forwards.AddForwardAsync(forward, ct);
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two clicks racing.
            return BadRequest(new { error = $"Local port {request.LocalPort} is already forwarded" });
        }
        await PolicyChangedAsync();
        return CreatedAtAction(nameof(GetForwards), View(forward, await _policy.GetAsync(ct)));
    }

    [HttpDelete("forwards/{id:guid}")]
    public async Task<IActionResult> DeleteForward(Guid id, CancellationToken ct)
    {
        if (!await _forwards.DeleteForwardAsync(id, ct)) return NotFound();
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

        var (entry, created) = await AddOrFindAsync(host, kind, providerId, ct);
        return created ? CreatedAtAction(nameof(GetEntries), View(entry)) : Ok(View(entry));
    }

    /// <summary>
    /// Adding an entry that already exists is a no-op that answers with the
    /// existing row: the read-before-insert covers the common case, and the
    /// unique index on (list, host, scope) covers two clicks racing, whose loser
    /// re-reads the winner instead of failing.
    /// </summary>
    private async Task<(NetworkPolicyEntry Entry, bool Created)> AddOrFindAsync(string host, NetworkListKind kind, Guid? providerId, CancellationToken ct)
    {
        if (await _store.FindEntryAsync(kind, host, providerId, ct) is { } existing)
            return (existing, false);

        var entry = new NetworkPolicyEntry
        {
            Id = Guid.NewGuid(),
            Host = host,
            ListKind = kind,
            AiProviderId = providerId,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            await _store.AddEntryAsync(entry, ct);
        }
        catch (DbUpdateException)
        {
            var winner = await _store.FindEntryAsync(kind, host, providerId, ct);
            if (winner is null) throw;
            return (winner, false);
        }
        await PolicyChangedAsync();
        return (entry, true);
    }

    private async Task PolicyChangedAsync()
    {
        _policy.Invalidate();
        await _notifier.PolicyChangedAsync();
    }

    private static object View(NetworkPolicyEntry e)
        => new { id = e.Id, host = e.Host, listKind = e.ListKind, aiProviderId = e.AiProviderId, createdAt = e.CreatedAt };

    /// <summary>
    /// A forward plus the two things about it that are not in its row: what the
    /// lists say about its destination right now, and whether its local port is
    /// actually bound.
    /// </summary>
    private object View(NetworkForwardEntry f, EgressPolicySnapshot policy)
        => new
        {
            id = f.Id,
            name = f.Name,
            host = f.Host,
            port = f.Port,
            localPort = f.LocalPort,
            createdAt = f.CreatedAt,
            decision = policy.Decide(f.Host, null),
            listenError = _forwarder.ListenErrorFor(f.Id),
        };

    private static object View(NetworkLogEntry l)
        => new { id = l.Id, host = l.Host, port = l.Port, timestamp = l.Timestamp, decision = l.Decision, aiProviderId = l.AiProviderId };
}
