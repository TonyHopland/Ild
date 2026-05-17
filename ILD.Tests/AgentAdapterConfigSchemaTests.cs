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

        Assert.Equal(5, adapter.ConfigSchema.Count());
        Assert.Equal("model", adapter.ConfigSchema[0].Name);
        Assert.Equal(ConfigFieldType.Text, adapter.ConfigSchema[0].Type);
        Assert.True(adapter.ConfigSchema[0].Required);
        Assert.Equal("default-model", adapter.ConfigSchema[0].DefaultValue);
        Assert.Equal(ConfigFieldType.Select, adapter.ConfigSchema[4].Type);
        Assert.Contains("us-east-1", adapter.ConfigSchema[4].Options!);
    }
}
