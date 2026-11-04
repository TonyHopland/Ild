using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// Every execution through an adapter the registry hands out holds the managed
/// agent's in-use lease for exactly its duration, so the auto-updater never
/// swaps the CLI under a run.
/// </summary>
public class AgentAdapterRegistryLeaseTests
{
    private sealed class Probe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Throw;
    }

    private sealed class BlockingAdapter(Probe probe) : IAgentAdapter
    {
        public string Name => "Blocking";
        public string[] SupportedProviderTypes => ["opencode", "custom"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

        public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
        {
            probe.Entered.TrySetResult();
            await probe.Release.Task;
            if (probe.Throw) throw new InvalidOperationException("adapter blew up");
            return NodeExecutionResult.Ok("done");
        }
    }

    private static (IAgentAdapterRegistry Registry, IManagedAgentUsage Usage, Probe Probe) Build()
    {
        var probe = new Probe();
        var usage = new ManagedAgentUsage();
        var sp = new ServiceCollection()
            .AddSingleton(probe)
            .AddSingleton<IManagedAgentUsage>(usage)
            .AddSingleton<IAgentAdapter, BlockingAdapter>()
            .AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>()
            .BuildServiceProvider();
        return (sp.GetRequiredService<IAgentAdapterRegistry>(), usage, probe);
    }

    private static AgentExecutionContext ContextFor(AiProvider provider)
        => new(
            provider,
            "prompt",
            new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "t", "d", "/tmp/none", "main", new List<string>(), null),
            1,
            CancellationToken.None);

    private static AiProvider Provider(string type) => new() { Name = "p", Type = type, BaseUrl = "", Model = "" };

    [Fact]
    public async Task The_lease_is_held_while_the_adapter_runs_and_released_on_success()
    {
        var (registry, usage, probe) = Build();
        var provider = Provider("opencode");

        var run = registry.ResolveForProvider(provider)().ExecuteAsync(ContextFor(provider));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(usage.IsBusy("opencode"));
        Assert.False(usage.IsBusy("pi"));

        probe.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(usage.IsBusy("opencode"));
    }

    [Fact]
    public async Task The_lease_is_released_when_the_adapter_throws()
    {
        var (registry, usage, probe) = Build();
        probe.Throw = true;
        var provider = Provider("OpenCode");

        var run = registry.ResolveForProvider(provider)().ExecuteAsync(ContextFor(provider));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(usage.IsBusy("opencode"));

        probe.Release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.False(usage.IsBusy("opencode"));
    }

    [Fact]
    public async Task A_provider_type_that_is_not_a_managed_agent_holds_no_lease()
    {
        var (registry, usage, probe) = Build();
        var provider = Provider("custom");

        var run = registry.ResolveForProvider(provider)().ExecuteAsync(ContextFor(provider));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        foreach (var agent in ManagedAgentCatalog.All)
            Assert.False(usage.IsBusy(agent.Key));

        probe.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
