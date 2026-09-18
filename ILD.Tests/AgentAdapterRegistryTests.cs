using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public class AgentAdapterRegistryTests
{
    private sealed class TestOpenCodeAdapter : IAgentAdapter
    {
        public string Name => "TestOpenCode";
        public string[] SupportedProviderTypes => ["opencode"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("opencode"));
    }

    private sealed class TestCustomAdapter : IAgentAdapter
    {
        public string Name => "TestCustom";
        public string[] SupportedProviderTypes => ["custom"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Required;
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("custom"));
    }

    private static ServiceProvider BuildServiceProvider(
        Action<ServiceCollection> configureAdapters)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        configureAdapters(services);
        services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ResolveForProvider_returns_factory_for_matching_adapter()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenCodeAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();
        var factory = registry.ResolveForProvider(new AiProvider { Type = "opencode" });

        Assert.IsType<TestOpenCodeAdapter>(factory());
    }

    [Fact]
    public void ResolveForProvider_picks_correct_adapter_when_multiple_registered()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenCodeAdapter>();
            s.AddSingleton<IAgentAdapter, TestCustomAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();

        Assert.IsType<TestOpenCodeAdapter>(registry.ResolveForProvider(new AiProvider { Type = "opencode" })());

        Assert.IsType<TestCustomAdapter>(registry.ResolveForProvider(new AiProvider { Type = "custom" })());
    }

    private sealed class TestCopilotAdapter : IAgentAdapter
    {
        public string Name => "TestCopilot";
        public string[] SupportedProviderTypes => ["copilot"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("copilot"));
    }

    [Fact]
    public void ResolveForProvider_ignores_the_case_of_the_saved_provider_type()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenCodeAdapter>();
            s.AddSingleton<IAgentAdapter, TestCopilotAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();

        Assert.IsType<TestCopilotAdapter>(registry.ResolveForProvider(new AiProvider { Type = "COPILOT" })());
        Assert.IsType<TestCopilotAdapter>(registry.ResolveForProvider(new AiProvider { Type = "Copilot" })());
    }

    [Fact]
    public void GetModelSupport_reports_what_each_adapter_declared()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenCodeAdapter>();
            s.AddSingleton<IAgentAdapter, TestCustomAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();

        Assert.Equal(AdapterModelSupport.Required, registry.GetModelSupport("custom"));
        Assert.Equal(AdapterModelSupport.Required, registry.GetModelSupport("CUSTOM"));
        Assert.Equal(AdapterModelSupport.Unsupported, registry.GetModelSupport("opencode"));
        // An unregistered type has nothing to declare, and is not an error to ask about.
        Assert.Equal(AdapterModelSupport.Unsupported, registry.GetModelSupport("unknown-type"));
    }

    [Fact]
    public void ResolveForProvider_throws_when_no_adapter_matches()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenCodeAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();

        var act = () => registry.ResolveForProvider(new AiProvider { Type = "unknown-type" })();

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("unknown-type", ex.Message);
    }

    [Fact]
    public void ResolveForProvider_factory_creates_new_instance_each_call()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenCodeAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();
        var factory = registry.ResolveForProvider(new AiProvider { Type = "opencode" });

        var first = factory();
        var second = factory();

        Assert.NotSame(second, first);
    }
}
