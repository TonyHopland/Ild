using FluentAssertions;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public class AgentAdaptersControllerTests
{
    private sealed class MockSchemaAdapter : IAgentAdapter
    {
        public string Name => "Mock";
        public string[] SupportedProviderTypes => ["mock"];
        public ConfigFieldDescriptor[] ConfigSchema => new ConfigFieldDescriptor[]
        {
            new("model", ConfigFieldType.Text, "Model", true, "gpt-4", null),
        };
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("ok"));
    }

    [Fact]
    public void GetConfigSchema_returns_schema_for_known_provider_type()
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<ILD.Data.Entities.AiProvider>()))
            .Returns(() => new MockSchemaAdapter());

        var controller = new AgentAdaptersController(registry.Object);

        var result = controller.GetConfigSchema("mock");

        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().BeOfType<ConfigFieldDescriptor[]>();
        ((ConfigFieldDescriptor[])okResult.Value!).First().Name.Should().Be("model");
    }

    [Fact]
    public void GetConfigSchema_returns_404_for_unknown_provider_type()
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<ILD.Data.Entities.AiProvider>()))
            .Throws(new InvalidOperationException("No adapter"));

        var controller = new AgentAdaptersController(registry.Object);

        var result = controller.GetConfigSchema("nonexistent");

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
