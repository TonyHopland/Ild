using System.ComponentModel.DataAnnotations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
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
/// setting value. Every edit goes through <see cref="NetworkPolicyEdits"/>, which invalidates the proxy's cached policy, so the
/// agent's next connection — and the forwarder's listener set — is judged by it.
/// </summary>
[ApiController]
[Route("api/v1/network")]
public class NetworkController : ControllerBase
{
    private const int DefaultLogTake = 200;
    private const int MaxLogTake = 1000;
    private const int DefaultDestinationDays = 7;
    private const int MaxDestinationDays = 90;
    private const string InstallersScope = "installers";

    private readonly INetworkPolicyStore _store;
    private readonly INetworkForwardStore _forwards;
    private readonly IEgressPolicy _policy;
    private readonly INetworkNotifier _notifier;
    private readonly NetworkEnforcementStatus _enforcement;
    private readonly IEgressForwarderState _forwarder;
    private readonly IRunHostOperator _runHost;
    private readonly NetworkPolicyEdits _edits;

    public NetworkController(
        INetworkPolicyStore store,
        INetworkForwardStore forwards,
        IEgressPolicy policy,
        INetworkNotifier notifier,
        NetworkEnforcementStatus enforcement,
        IEgressForwarderState forwarder,
        IRunHostOperator runHost,
        NetworkPolicyEdits edits)
    {
        _runHost = runHost;
        _edits = edits;
        _store = store;
        _forwards = forwards;
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

        /// <summary>The installers' scope (toolchain installers, ADR-0035); not with a provider.</summary>
        public bool Installers { get; set; }
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

    public sealed class AddAllowedRequest
    {
        /// <summary><c>global</c>, <c>installers</c> or <c>provider</c> (with <see cref="AiProviderId"/>).</summary>
        [Required]
        public string Scope { get; set; } = string.Empty;

        public Guid? AiProviderId { get; set; }

        [Required]
        public List<string> Entries { get; set; } = [];
    }

    public sealed class AddFromLogRequest
    {
        /// <summary>Scope the new entry to the log entry's provider instead of making it global.</summary>
        public bool ScopeToProvider { get; set; }

        /// <summary>Scope the new entry to the installers instead of making it global; for a destination a toolchain installer asked for.</summary>
        public bool ScopeToInstallers { get; set; }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
        => Ok(new
        {
            enforcement = _enforcement.EnforcementFor(_runHost.Kind),
            reason = _enforcement.ReasonFor(_runHost.Kind),
            contained = _runHost.Kind != RunHostKind.Local,
            proxyEnabled = _enforcement.ProxyEnabled,
            proxyPort = _enforcement.ProxyPort,
            directRoutes = (await DirectRoutesAsync(ct)).Select(r => new { ownerId = r.OwnerId, chat = r.Chat }),
        });

    // The runs and chats that have a direct route (network mode Off) now. The page shows the rest of the status when the engine or cluster cannot say.
    private async Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct)
    {
        try { return await _runHost.DirectRoutesAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    public sealed class SetProtectedRequest
    {
        public bool Enabled { get; set; }
    }

    [HttpGet("protected")]
    public async Task<IActionResult> GetProtected(CancellationToken ct)
        => Ok((await _policy.GetAsync(ct)).Protected.Select(View));

    [HttpPut("protected/{id}")]
    public async Task<IActionResult> SetProtected(string id, [FromBody] SetProtectedRequest request, CancellationToken ct)
        => await _edits.SetProtectedAsync(id, request.Enabled, ct) is { } updated ? Ok(View(updated)) : NotFound();

    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken ct)
        => Ok((await _store.GetEntriesAsync(ct)).Select(View));

    [HttpPost("entries")]
    public async Task<IActionResult> AddEntry([FromBody] AddEntryRequest request, CancellationToken ct)
        => EntryResult(await _edits.AddEntryAsync(request.Host, request.ListKind, request.AiProviderId, request.Installers, ct));

    [HttpPost("entries/bulk")]
    public async Task<IActionResult> AddAllowed([FromBody] AddAllowedRequest request, CancellationToken ct)
    {
        if (!TryReadScope(request.Scope, request.AiProviderId, out var installers, out var scopeError))
            return BadRequest(new { error = scopeError });
        var result = await _edits.AddAllowedAsync(request.Entries, request.AiProviderId, installers, ct);
        return result.Outcome == NetworkEditOutcome.Refused
            ? BadRequest(new { error = result.Error })
            : Ok(new { created = result.Value!.Select(View) });
    }

    [HttpPut("entries/{id:guid}")]
    public async Task<IActionResult> UpdateEntry(Guid id, [FromBody] AddEntryRequest request, CancellationToken ct)
        => EntryResult(await _edits.UpdateEntryAsync(id, request.Host, request.ListKind, request.AiProviderId, request.Installers, ct));

    [HttpDelete("entries/{id:guid}")]
    public async Task<IActionResult> DeleteEntry(Guid id, CancellationToken ct)
        => await _edits.DeleteEntryAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("forwards")]
    public async Task<IActionResult> GetForwards(CancellationToken ct)
    {
        var policy = await _policy.GetAsync(ct);
        return Ok((await _forwards.GetForwardsAsync(ct)).Select(f => View(f, policy)));
    }

    [HttpPost("forwards")]
    public async Task<IActionResult> AddForward([FromBody] AddForwardRequest request, CancellationToken ct)
        => await ForwardResultAsync(await _edits.AddForwardAsync(request.Name, request.Host, request.Port, request.LocalPort, ct), ct);

    [HttpPut("forwards/{id:guid}")]
    public async Task<IActionResult> UpdateForward(Guid id, [FromBody] AddForwardRequest request, CancellationToken ct)
        => await ForwardResultAsync(await _edits.UpdateForwardAsync(id, request.Name, request.Host, request.Port, request.LocalPort, ct), ct);

    [HttpDelete("forwards/{id:guid}")]
    public async Task<IActionResult> DeleteForward(Guid id, CancellationToken ct)
        => await _edits.DeleteForwardAsync(id, ct) ? NoContent() : NotFound();

    private IActionResult EntryResult(NetworkEditResult<NetworkPolicyEntry> result) => result.Outcome switch
    {
        NetworkEditOutcome.Created => CreatedAtAction(nameof(GetEntries), View(result.Value!)),
        NetworkEditOutcome.Refused => BadRequest(new { error = result.Error }),
        NetworkEditOutcome.NotFound => NotFound(),
        _ => Ok(View(result.Value!)),
    };

    private async Task<IActionResult> ForwardResultAsync(NetworkEditResult<NetworkForwardEntry> result, CancellationToken ct)
    {
        if (result.Outcome == NetworkEditOutcome.Refused) return BadRequest(new { error = result.Error });
        if (result.Outcome == NetworkEditOutcome.NotFound) return NotFound();
        var view = View(result.Value!, await _policy.GetAsync(ct));
        return result.Outcome == NetworkEditOutcome.Created ? CreatedAtAction(nameof(GetForwards), view) : Ok(view);
    }

    [HttpGet("log")]
    public async Task<IActionResult> GetLog([FromQuery] int take = DefaultLogTake, CancellationToken ct = default)
        => Ok((await _store.GetLogAsync(Math.Clamp(take, 1, MaxLogTake), ct)).Select(View));

    /// <summary>The distinct hosts the log shows for a scope in the last <paramref name="days"/> days, with whether the whitelist already admits each.</summary>
    [HttpGet("log/destinations")]
    public async Task<IActionResult> GetLogDestinations([FromQuery] string scope, [FromQuery] Guid? providerId, [FromQuery] int days = DefaultDestinationDays, CancellationToken ct = default)
    {
        if (!TryReadScope(scope, providerId, out var installers, out var error))
            return BadRequest(new { error });
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, MaxDestinationDays));
        var entries = await _store.GetEntriesAsync(ct);
        var rows = await _store.GetLogDestinationsAsync(providerId, installers, since, ct);
        return Ok(rows
            .GroupBy(d => d.Host)
            .Select(g => new
            {
                host = g.Key,
                count = g.Sum(d => d.Count),
                lastSeen = g.Max(d => d.LastSeen),
                allowed = g.All(d => EgressRules.Decide(NetworkMode.Whitelist, entries, g.Key, d.AiProviderId, installers ? InstallersScope : null) == NetworkDecision.Allowed),
            })
            .OrderByDescending(d => d.count)
            .ThenBy(d => d.host));
    }

    private static bool TryReadScope(string? scope, Guid? providerId, out bool installers, out string error)
    {
        installers = false;
        error = string.Empty;
        switch (scope)
        {
            case "global" when providerId is null:
                return true;
            case InstallersScope when providerId is null:
                installers = true;
                return true;
            case "provider" when providerId is not null:
                return true;
            default:
                error = "scope must be 'global', 'installers' or 'provider' (with providerId)";
                return false;
        }
    }

    [HttpDelete("log")]
    public async Task<IActionResult> ClearLog(CancellationToken ct)
    {
        var removed = await _store.ClearLogAsync(ct);
        await _notifier.LogClearedAsync();
        return Ok(new { removed });
    }

    [HttpPost("log/{id:guid}/whitelist")]
    public Task<IActionResult> WhitelistFromLog(Guid id, [FromBody] AddFromLogRequest? request, CancellationToken ct)
        => AddFromLogAsync(id, NetworkListKind.Whitelist, request?.ScopeToProvider ?? false, request?.ScopeToInstallers ?? false, ct);

    [HttpPost("log/{id:guid}/blacklist")]
    public Task<IActionResult> BlacklistFromLog(Guid id, [FromBody] AddFromLogRequest? request, CancellationToken ct)
        => AddFromLogAsync(id, NetworkListKind.Blacklist, request?.ScopeToProvider ?? false, request?.ScopeToInstallers ?? false, ct);

    private async Task<IActionResult> AddFromLogAsync(Guid logId, NetworkListKind kind, bool scopeToProvider, bool scopeToInstallers, CancellationToken ct)
        => EntryResult(await _edits.AddFromLogAsync(logId, kind, scopeToProvider, scopeToInstallers, ct));

    private static object View(NetworkPolicyEntry e)
        => new { id = e.Id, host = e.Host, listKind = e.ListKind, aiProviderId = e.AiProviderId, installers = e.Installers, createdAt = e.CreatedAt };

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

    private static object View(ProtectedDestination p)
        => new { id = p.Id, name = p.Name, hosts = p.Hosts, addresses = p.Addresses.Select(a => a.ToString()), ranges = p.Ranges.Select(r => r.ToString()), enabled = p.Enabled };

    private static object View(NetworkLogEntry l)
        => new { id = l.Id, host = l.Host, port = l.Port, timestamp = l.Timestamp, decision = l.Decision, aiProviderId = l.AiProviderId, installer = l.Installer, reason = l.Reason, via = l.Via };
}
