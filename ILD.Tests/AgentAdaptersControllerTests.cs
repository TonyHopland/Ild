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
