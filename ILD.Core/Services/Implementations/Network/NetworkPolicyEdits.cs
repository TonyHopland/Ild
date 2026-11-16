using ILD.Data;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>What an edit came to; <see cref="Refused"/> carries the reason the operator sees.</summary>
public enum NetworkEditOutcome
{
    Created,
    Existing,
    Updated,
    NotFound,
    Refused,
}

public readonly record struct NetworkEditResult<T>(NetworkEditOutcome Outcome, T? Value = default, string? Error = null) where T : class
{
    internal static NetworkEditResult<T> Refuse(string error) => new(NetworkEditOutcome.Refused, null, error);
    internal static NetworkEditResult<T> Missing => new(NetworkEditOutcome.NotFound);
}

/// <summary>
/// The one place the network policy is edited (ADR-0019, ADR-0020): the entry and forward rules, the races two edits can run
/// into (the unique indexes answer what a read first could not), the mode, the protected destinations and the entries ILD adds
/// for a provider. The egress proxy caches the policy for a second, so an edit that changed anything invalidates it and then
/// tells the listeners, in that order, here and nowhere else; an edit that is refused does neither.
/// </summary>
public sealed partial class NetworkPolicyEdits(
    INetworkPolicyStore entries,
    INetworkForwardStore forwards,
    IProviderStore providers,
    IAppSettingStore settings,
    AppDbContext db,
    IEgressPolicy policy,
    INetworkNotifier notifier)
{
    private const int MaxForwardNameLength = 128;

    private async Task ChangedAsync()
    {
        policy.Invalidate();
        await notifier.PolicyChangedAsync();
    }

    /// <summary>The mode is an app setting (<c>network.mode</c>); the proxy judges the next connection by it and re-judges the open tunnels.</summary>
    public async Task SetModeAsync(string value, CancellationToken ct = default)
    {
        await settings.UpsertAsync(AppSettingKeys.NetworkMode, value, ct);
        await ChangedAsync();
    }

    /// <summary>Switches a protected destination on or off. Null when there is none with this id.</summary>
    public async Task<ProtectedDestination?> SetProtectedAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var current = (await policy.GetAsync(ct)).Protected.FirstOrDefault(p => p.Id == id);
        if (current is null) return null;
        await settings.UpsertAsync(ProtectedDestination.SettingKey(id), enabled ? "true" : "false", ct);
        await ChangedAsync();
        return current with { Enabled = enabled };
    }

    /// <summary>
    /// Adding an entry that already exists is a no-op that answers with the existing row: the read-before-insert covers the
    /// common case, and the unique index on (list, host, scope) covers two clicks racing, whose loser re-reads the winner.
    /// </summary>
    public async Task<NetworkEditResult<NetworkPolicyEntry>> AddEntryAsync(string host, NetworkListKind kind, Guid? providerId, bool installers, CancellationToken ct = default)
    {
        var valid = await ValidateEntryAsync(host, kind, providerId, installers);
        if (valid.Error is not null) return NetworkEditResult<NetworkPolicyEntry>.Refuse(valid.Error);
        return await AddOrFindAsync(valid.Host, kind, providerId, installers, ct);
    }

    /// <summary>Adds the host of a logged connection to a list, as itself: a pattern an agent sent is refused, never widened into a rule.</summary>
    public async Task<NetworkEditResult<NetworkPolicyEntry>> AddFromLogAsync(Guid logId, NetworkListKind kind, bool scopeToProvider, bool scopeToInstallers, CancellationToken ct = default)
    {
        var logged = await entries.GetLogEntryAsync(logId, ct);
        if (logged is null) return NetworkEditResult<NetworkPolicyEntry>.Missing;
        if (EgressRules.LooksLikePattern(logged.Host.Trim()))
            return NetworkEditResult<NetworkPolicyEntry>.Refuse("This host was sent as a pattern, not a host name, so it cannot be added from the log");
        if (!EgressRules.TryNormalizePattern(logged.Host, out var host, out var error))
            return NetworkEditResult<NetworkPolicyEntry>.Refuse(error);

        var providerId = scopeToProvider ? logged.AiProviderId : null;
        if (providerId is { } id && await providers.GetAiProviderByIdAsync(id) is null)
            providerId = null;
        var installers = scopeToInstallers && logged.Installer is not null;

        return await AddOrFindAsync(host, kind, providerId, installers, ct);
    }

    public async Task<NetworkEditResult<NetworkPolicyEntry>> UpdateEntryAsync(Guid id, string host, NetworkListKind kind, Guid? providerId, bool installers, CancellationToken ct = default)
    {
        var valid = await ValidateEntryAsync(host, kind, providerId, installers);
        if (valid.Error is not null) return NetworkEditResult<NetworkPolicyEntry>.Refuse(valid.Error);
        host = valid.Host;
        if (await entries.FindEntryAsync(kind, host, providerId, ct, installers) is { } other && other.Id != id)
            return NetworkEditResult<NetworkPolicyEntry>.Refuse(RuleExists(kind, host));

        NetworkPolicyEntry? entry;
        try
        {
            entry = await entries.UpdateEntryAsync(id, host, kind, providerId, ct, installers);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted between the load and the save.
            return NetworkEditResult<NetworkPolicyEntry>.Missing;
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two requests racing.
            return NetworkEditResult<NetworkPolicyEntry>.Refuse(RuleExists(kind, host));
        }
        if (entry is null) return NetworkEditResult<NetworkPolicyEntry>.Missing;
        await ChangedAsync();
        return new(NetworkEditOutcome.Updated, entry);
    }

    /// <summary>False when there is no entry with this id.</summary>
    public async Task<bool> DeleteEntryAsync(Guid id, CancellationToken ct = default)
    {
        if (!await entries.DeleteEntryAsync(id, ct)) return false;
        await ChangedAsync();
        return true;
    }

    public async Task<NetworkEditResult<NetworkForwardEntry>> AddForwardAsync(string? name, string host, int port, int localPort, CancellationToken ct = default)
    {
        if (ValidateForward(name, host, port, localPort, out var cleanName, out var cleanHost) is { } invalid)
            return NetworkEditResult<NetworkForwardEntry>.Refuse(invalid);
        if (await forwards.FindByLocalPortAsync(localPort, ct) is { } taken)
            return NetworkEditResult<NetworkForwardEntry>.Refuse(LocalPortTaken(taken));

        var forward = new NetworkForwardEntry
        {
            Id = Guid.NewGuid(),
            Name = cleanName,
            Host = cleanHost,
            Port = port,
            LocalPort = localPort,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            await forwards.AddForwardAsync(forward, ct);
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two requests racing.
            return NetworkEditResult<NetworkForwardEntry>.Refuse(LocalPortRaced(localPort));
        }
        await ChangedAsync();
        return new(NetworkEditOutcome.Created, forward);
    }

    public async Task<NetworkEditResult<NetworkForwardEntry>> UpdateForwardAsync(Guid id, string? name, string host, int port, int localPort, CancellationToken ct = default)
    {
        if (ValidateForward(name, host, port, localPort, out var cleanName, out var cleanHost) is { } invalid)
            return NetworkEditResult<NetworkForwardEntry>.Refuse(invalid);
        if (await forwards.FindByLocalPortAsync(localPort, ct) is { } taken && taken.Id != id)
            return NetworkEditResult<NetworkForwardEntry>.Refuse(LocalPortTaken(taken));

        NetworkForwardEntry? forward;
        try
        {
            forward = await forwards.UpdateForwardAsync(id, cleanName, cleanHost, port, localPort, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted between the load and the save.
            return NetworkEditResult<NetworkForwardEntry>.Missing;
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two requests racing.
            return NetworkEditResult<NetworkForwardEntry>.Refuse(LocalPortRaced(localPort));
        }
        if (forward is null) return NetworkEditResult<NetworkForwardEntry>.Missing;
        await ChangedAsync();
        return new(NetworkEditOutcome.Updated, forward);
    }

    /// <summary>False when there is no forward with this id.</summary>
    public async Task<bool> DeleteForwardAsync(Guid id, CancellationToken ct = default)
    {
        if (!await forwards.DeleteForwardAsync(id, ct)) return false;
        await ChangedAsync();
        return true;
    }

    private async Task<NetworkEditResult<NetworkPolicyEntry>> AddOrFindAsync(string host, NetworkListKind kind, Guid? providerId, bool installers, CancellationToken ct)
    {
        if (await entries.FindEntryAsync(kind, host, providerId, ct, installers) is { } existing)
            return new(NetworkEditOutcome.Existing, existing);

        var entry = new NetworkPolicyEntry
        {
            Id = Guid.NewGuid(),
            Host = host,
            ListKind = kind,
            AiProviderId = providerId,
            Installers = installers,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            await entries.AddEntryAsync(entry, ct);
        }
        catch (DbUpdateException)
        {
            var winner = await entries.FindEntryAsync(kind, host, providerId, ct, installers);
            if (winner is null) throw;
            return new(NetworkEditOutcome.Existing, winner);
        }
        await ChangedAsync();
        return new(NetworkEditOutcome.Created, entry);
    }

    private async Task<(string? Error, string Host)> ValidateEntryAsync(string host, NetworkListKind kind, Guid? providerId, bool installers)
    {
        if (!Enum.IsDefined(kind))
            return ("listKind must be 'Whitelist' or 'Blacklist'", string.Empty);
        if (!EgressRules.TryNormalizePattern(host, out var normalized, out var error))
            return (error, string.Empty);
        if (installers && providerId is not null)
            return ("An entry is for the installers or for one provider, not both", string.Empty);
        if (providerId is { } id && await providers.GetAiProviderByIdAsync(id) is null)
            return ("Unknown AI provider", string.Empty);
        return (null, normalized);
    }

    private static string RuleExists(NetworkListKind kind, string host)
        => $"A {kind.ToString().ToLowerInvariant()} rule for {host} already exists for this scope";

    private static string LocalPortTaken(NetworkForwardEntry taken)
        => $"Local port {taken.LocalPort} already forwards to {taken.Host}:{taken.Port}";

    private static string LocalPortRaced(int localPort)
        => $"Local port {localPort} is already forwarded";

    private static string? ValidateForward(string? rawName, string host, int port, int localPort, out string name, out string normalizedHost)
    {
        normalizedHost = string.Empty;
        name = (rawName ?? string.Empty).Trim();
        if (name.Length == 0)
            return "Give the forward a name, e.g. postgres";
        if (name.Length > MaxForwardNameLength)
            return $"Names are at most {MaxForwardNameLength} characters";
        if (!EgressRules.TryNormalizeForwardHost(host, out normalizedHost, out var error))
            return error;
        if (port is < 1 or > 65535)
            return "The destination port must be between 1 and 65535";
        if (localPort is < 1 or > 65535)
            return "The local port must be between 1 and 65535";
        return null;
    }
}
