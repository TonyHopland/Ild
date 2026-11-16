using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations.Network;

// The entries ILD keeps for the model gateway's upstream hosts, scoped to the provider that uses them (ADR-0031, ADR-0019): the
// gateway is judged by the network policy, so without them whitelist mode would stop every model call. The entries ILD adds are
// marked AddedForProvider; an entry the operator made for the same host stays the operator's and is neither replaced nor
// removed. An operator who deletes one gets the refusal, which is the point.
public sealed partial class NetworkPolicyEdits
{
    /// <summary>Set once the entries of the providers that existed before this feature have been added.</summary>
    public const string SeededKey = "network.gatewayEntriesSeeded";

    /// <summary>
    /// Makes the provider's ILD-added entries exactly the hosts its type and base URL need: adds the missing ones and removes
    /// the ones that no longer apply. Returns whether anything changed.
    /// </summary>
    public async Task<bool> SyncProviderAsync(AiProvider provider, CancellationToken ct = default)
    {
        var changed = Apply(provider, await ScopedEntriesAsync(provider.Id, ct));
        if (changed)
        {
            await db.SaveChangesAsync(ct);
            await ChangedAsync();
        }
        return changed;
    }

    /// <summary>The upgrade path: syncs every existing provider, once per database. Returns the number of providers that gained or lost an entry.</summary>
    public async Task<int> SeedProviderEntriesAsync(CancellationToken ct = default)
    {
        if (await db.AppSettings.AnyAsync(s => s.Key == SeededKey, ct)) return 0;

        var entries = (await db.NetworkPolicyEntries
                .Where(e => e.AiProviderId != null && e.ListKind == NetworkListKind.Whitelist && !e.Installers)
                .ToListAsync(ct))
            .ToLookup(e => e.AiProviderId!.Value);
        var changed = 0;
        foreach (var provider in await db.AiProviders.AsNoTracking().ToListAsync(ct))
            if (Apply(provider, entries[provider.Id].ToList())) changed++;
        db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = SeededKey, Value = "done" });
        await db.SaveChangesAsync(ct);
        if (changed > 0) await ChangedAsync();
        return changed;
    }

    private Task<List<NetworkPolicyEntry>> ScopedEntriesAsync(Guid providerId, CancellationToken ct)
        => db.NetworkPolicyEntries
            .Where(e => e.AiProviderId == providerId && e.ListKind == NetworkListKind.Whitelist && !e.Installers)
            .ToListAsync(ct);

    private bool Apply(AiProvider provider, List<NetworkPolicyEntry> scoped)
    {
        var wanted = GatewayRoutes.UpstreamHosts(provider);
        var changed = false;
        foreach (var stale in scoped.Where(e => e.AddedForProvider && !wanted.Contains(e.Host, StringComparer.Ordinal)))
        {
            db.NetworkPolicyEntries.Remove(stale);
            changed = true;
        }
        foreach (var host in wanted.Where(h => scoped.All(e => e.Host != h)))
        {
            db.NetworkPolicyEntries.Add(new NetworkPolicyEntry
            {
                Id = Guid.NewGuid(),
                Host = host,
                ListKind = NetworkListKind.Whitelist,
                AiProviderId = provider.Id,
                AddedForProvider = true,
                CreatedAt = DateTime.UtcNow,
            });
            changed = true;
        }
        return changed;
    }
}
