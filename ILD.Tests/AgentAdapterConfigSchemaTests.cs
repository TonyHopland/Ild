using FluentAssertions;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public class AgentAdapterConfigSchemaTests
{
    private sealed class TestSchemaAdapter : IAgentAdapter
    {
        public string Name => "TestSchema";
        public string[] SupportedProviderTypes => ["test-schema"];
        public ConfigFieldDescriptor[] ConfigSchema => new ConfigFieldDescriptor[]
        {
            new("model", ConfigFieldType.Text, "Model", true, "default-model", "The model to use"),
            new("temperature", ConfigFieldType.Number, "Temperature", false, 0.7, "Controls randomness"),
            new("enableTools", ConfigFieldType.Toggle, "Enable Tools", false, true, "Whether tools are enabled"),
            new("systemPrompt", ConfigFieldType.Textarea, "System Prompt", false, null, "A system prompt"),
            new("region", ConfigFieldType.Select, "Region", false, "us-east-1", "Deployment region", new[] { "us-east-1", "eu-west-1" }),
        };
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("ok"));
    }

    private static ServiceProvider BuildServiceProvider(
        Action<ServiceCollection> configureAdapters)
    {
        var services = new ServiceCollection();
        configureAdapters(services);
        services.AddSingleton<IEnumerable<ServiceDescriptor>>(services);
        services.AddSingleton<IAgentAdapterRegistry, ILD.Core.Services.Implementations.AgentAdapterRegistry>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigSchema_returns_field_descriptors_for_registered_adapter()
    {
        var sp = BuildServiceProvider(s =>
        {
            s.AddSingleton<IAgentAdapter, TestSchemaAdapter>();
        });

        var registry = sp.GetRequiredService<IAgentAdapterRegistry>();
        var factory = registry.ResolveForProvider(new ILD.Data.Entities.AiProvider { Type = "test-schema" });
        var adapter = factory();

        adapter.ConfigSchema.Should().HaveCount(5);
        adapter.ConfigSchema[0].Name.Should().Be("model");
        adapter.ConfigSchema[0].Type.Should().Be(ConfigFieldType.Text);
        adapter.ConfigSchema[0].Required.Should().BeTrue();
        adapter.ConfigSchema[0].DefaultValue.Should().Be("default-model");
        adapter.ConfigSchema[4].Type.Should().Be(ConfigFieldType.Select);
        adapter.ConfigSchema[4].Options.Should().Contain("us-east-1");
    }
}
