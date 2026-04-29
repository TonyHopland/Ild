using FluentAssertions;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public class AgentAdapterRegistryTests
{
    private sealed class TestOpenAiAdapter : IAgentAdapter
    {
        public string Name => "TestOpenAi";
        public string[] SupportedProviderTypes => ["openai"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("openai"));
    }

    private sealed class TestCustomAdapter : IAgentAdapter
    {
        public string Name => "TestCustom";
        public string[] SupportedProviderTypes => ["custom"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("custom"));
    }

    private static ServiceProvider BuildServiceProvider(
        Action<ServiceCollection> configureAdapters)
    {
        var services = new ServiceCollection();
        configureAdapters(services);
        services.AddSingleton<IEnumerable<ServiceDescriptor>>(services);
        services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ResolveForProvider_returns_factory_for_matching_adapter()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenAiAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();
        var factory = registry.ResolveForProvider(new AiProvider { Type = "openai" });

        factory().Should().BeOfType<TestOpenAiAdapter>();
    }

    [Fact]
    public void ResolveForProvider_picks_correct_adapter_when_multiple_registered()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenAiAdapter>();
            s.AddSingleton<IAgentAdapter, TestCustomAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();

        registry.ResolveForProvider(new AiProvider { Type = "openai" })()
            .Should().BeOfType<TestOpenAiAdapter>();

        registry.ResolveForProvider(new AiProvider { Type = "custom" })()
            .Should().BeOfType<TestCustomAdapter>();
    }

    [Fact]
    public void ResolveForProvider_throws_when_no_adapter_matches()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenAiAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();

        var act = () => registry.ResolveForProvider(new AiProvider { Type = "unknown-type" })();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown-type*");
    }

    [Fact]
    public void ResolveForProvider_factory_creates_new_instance_each_call()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestOpenAiAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();
        var factory = registry.ResolveForProvider(new AiProvider { Type = "openai" });

        var first = factory();
        var second = factory();

        first.Should().NotBeSameAs(second);
    }
}
