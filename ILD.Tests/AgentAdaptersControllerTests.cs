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

        Assert.IsType<OkObjectResult>(result);
        var okResult = (OkObjectResult)result;
        Assert.IsType<ConfigFieldDescriptor[]>(okResult.Value);
        Assert.Equal("model", ((ConfigFieldDescriptor[])okResult.Value!).First().Name);
    }

    [Fact]
    public void GetConfigSchema_returns_404_for_unknown_provider_type()
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<ILD.Data.Entities.AiProvider>()))
            .Throws(new InvalidOperationException("No adapter"));

        var controller = new AgentAdaptersController(registry.Object);

        var result = controller.GetConfigSchema("nonexistent");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetSupportedProviderTypes_returns_all_registered_types()
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.GetAllSupportedProviderTypes())
            .Returns(new[] { "opencode", "pi" });

        var controller = new AgentAdaptersController(registry.Object);

        var result = controller.GetSupportedProviderTypes();

        Assert.IsType<OkObjectResult>(result);
        var okResult = (Microsoft.AspNetCore.Mvc.OkObjectResult)result;
        var types = (string[])okResult.Value!;
        Assert.Contains("opencode", types);
        Assert.Contains("pi", types);
    }
}
