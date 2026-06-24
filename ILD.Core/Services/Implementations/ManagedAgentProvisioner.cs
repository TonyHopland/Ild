using System.Collections.Concurrent;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Installs the managed coding agents that configured AI providers need, so a
/// fresh or freshly-upgraded deployment (agents no longer baked into the image)
/// doesn't fail the first AI run on a missing CLI. As an <see cref="IHostedService"/>
/// it provisions every agent an existing provider uses at startup; the
/// <see cref="IManagedAgentProvisioner"/> entry point provisions one on demand when
/// a provider is added. Both go through the same dedup + background-install path.
/// Installs run detached — never blocking host startup or an HTTP request — and a
/// failure is logged, leaving the manual Install button as the fallback.
/// </summary>
public sealed class ManagedAgentProvisioner : IManagedAgentProvisioner, IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ManagedAgentProvisioner> _log;

    // Agent keys with an ensure already queued or running. Stops a burst of
    // provider adds (or startup racing an add) from piling up duplicate installs;
    // ManagedAgentService's own static per-agent lock still serializes the npm work.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly CancellationTokenSource _stopping = new();

    public ManagedAgentProvisioner(IServiceScopeFactory scopes, ILogger<ManagedAgentProvisioner> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // npm installs are minutes-long and network-bound — never block host
        // startup on them. Enumerate providers and kick off the ensures detached.
        _ = Task.Run(EnsureForExistingProvidersAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void EnsureInstalledForProviderType(string? providerType)
    {
        if (ManagedAgentCatalog.Find(providerType) is { } agent)
            QueueEnsure(agent.Key);
    }

    private async Task EnsureForExistingProvidersAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var providers = await scope.ServiceProvider
                .GetRequiredService<IProviderStore>()
                .GetAllAiProvidersAsync();

            var keys = providers
                .Select(p => ManagedAgentCatalog.Find(p.Type)?.Key)
                .Where(k => k is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (keys.Count > 0)
                _log.LogInformation(
                    "Provisioning {Count} managed coding agent(s) used by existing AI providers", keys.Count);

            foreach (var key in keys)
                QueueEnsure(key!);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to provision managed coding agents at startup");
        }
    }

    private void QueueEnsure(string agentKey)
    {
        if (_stopping.IsCancellationRequested) return;
        // An ensure for this agent is already pending/running — let it cover us.
        if (!_inFlight.TryAdd(agentKey, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IManagedAgentService>();
                var status = await service.EnsureInstalledAsync(agentKey, _stopping.Token);

                if (status.InstalledVersion is not null)
                    _log.LogInformation(
                        "Managed agent {Agent} is installed ({Version})", agentKey, status.InstalledVersion);
                else
                    _log.LogWarning(
                        "Managed agent {Agent} could not be auto-installed ({Error}); install it from the AI Provider page",
                        agentKey, status.Error ?? "unknown error");
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Host shutting down mid-install — the staging dir is cleaned up
                // by the install path; provisioning resumes on next startup.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Automatic install of managed agent {Agent} failed", agentKey);
            }
            finally
            {
                _inFlight.TryRemove(agentKey, out _);
            }
        });
    }
}
