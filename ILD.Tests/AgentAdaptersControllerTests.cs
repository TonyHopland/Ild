using ILD.Api.Configuration;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;
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

    private static AgentAdapterDescriptor[] ListAdapters(params (string Type, AdapterModelSupport Support)[] adapters)
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.GetAllSupportedProviderTypes())
            .Returns(adapters.Select(a => a.Type).ToArray());
        foreach (var (type, support) in adapters)
            registry.Setup(r => r.GetModelSupport(type)).Returns(support);
        registry.Setup(r => r.GetTraits(It.IsAny<string?>())).Returns(AgentAdapterTraits.None);

        var result = new AgentAdaptersController(registry.Object).GetSupportedProviderTypes();

        return (AgentAdapterDescriptor[])Assert.IsType<OkObjectResult>(result).Value!;
    }

    [Fact]
    public void GetSupportedProviderTypes_returns_all_registered_types()
    {
        var adapters = ListAdapters(
            ("opencode", AdapterModelSupport.Required),
            ("pi", AdapterModelSupport.Required));

        Assert.Equal(["opencode", "pi"], adapters.Select(a => a.Type));
    }

    [Fact]
    public void GetSupportedProviderTypes_carries_each_adapters_declared_model_support()
    {
        var adapters = ListAdapters(
            ("pi", AdapterModelSupport.Required),
            ("claude-code", AdapterModelSupport.Optional),
            ("mock", AdapterModelSupport.Unsupported));

        Assert.Equal(AdapterModelSupport.Required, adapters.Single(a => a.Type == "pi").ModelSupport);
        Assert.Equal(AdapterModelSupport.Optional, adapters.Single(a => a.Type == "claude-code").ModelSupport);
        Assert.Equal(AdapterModelSupport.Unsupported, adapters.Single(a => a.Type == "mock").ModelSupport);
    }
    [Fact]
    public void The_registered_adapters_offer_codex_with_an_optional_model_and_the_mcp_and_extra_argument_fields()
    {
        using var provider = RegisteredAdapters();
        var controller = new AgentAdaptersController(provider.GetRequiredService<IAgentAdapterRegistry>());

        var listed = (AgentAdapterDescriptor[])Assert.IsType<OkObjectResult>(controller.GetSupportedProviderTypes()).Value!;
        Assert.Equal(AdapterModelSupport.Optional, Assert.Single(listed, a => a.Type == "codex").ModelSupport);
        var schema = (ConfigFieldDescriptor[])Assert.IsType<OkObjectResult>(controller.GetConfigSchema("Codex")).Value!;
        Assert.Equal(["customMcpServersJson", "extraArgs"], schema.Select(f => f.Name).Order());
    }

    private static ServiceProvider RegisteredAdapters()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        foreach (var descriptor in new ServiceCollection().AddIldServices().Where(d =>
                     d.ServiceType == typeof(IAgentAdapter)
                     || d.ServiceType == typeof(IAgentAdapterRegistry)
                     || d.ServiceType == typeof(IProcessEnvironment)))
            services.Add(descriptor);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_registered_adapters_list_each_type_with_its_display_name_and_how_its_cli_signs_in()
    {
        using var provider = RegisteredAdapters();

        var listed = ((AgentAdapterDescriptor[])Assert.IsType<OkObjectResult>(
                new AgentAdaptersController(provider.GetRequiredService<IAgentAdapterRegistry>()).GetSupportedProviderTypes()).Value!)
            .ToDictionary(a => a.Type);

        Assert.Equal("OpenCode", listed["opencode"].DisplayName);
        Assert.Equal("Pi", listed["pi"].DisplayName);
        Assert.Equal("Claude Code", listed["claude-code"].DisplayName);
        Assert.Equal("Copilot", listed["copilot"].DisplayName);
        Assert.Equal("Codex", listed["codex"].DisplayName);

        // The BYO-endpoint types sign in with a Base URL and API key, not through a CLI.
        Assert.Null(listed["opencode"].CliAuth);
        Assert.Null(listed["pi"].CliAuth);

        var claude = listed["claude-code"].CliAuth!;
        Assert.Equal("Claude Code", claude.CliName);
        Assert.Contains("`/login`", claude.SignInStep);
        Assert.Contains("Claude Code subscription", claude.SignInStep);

        var copilot = listed["copilot"].CliAuth!;
        Assert.Equal("GitHub Copilot", copilot.CliName);
        Assert.Contains("`/login`", copilot.SignInStep);

        var codex = listed["codex"].CliAuth!;
        Assert.Equal("Codex", codex.CliName);
        Assert.Contains("**Sign in with Device Code**", codex.SignInStep);
        Assert.Contains("ChatGPT account", codex.SignInStep);
        Assert.DoesNotContain("/login", codex.SignInStep);
    }
}
