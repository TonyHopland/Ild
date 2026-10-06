using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;

namespace ILD.Tests;

public class AdapterConfigSchemaTests
{
    [Fact]
    public void OpenCodeAdapter_schema_excludes_provider_level_fields()
    {
        var adapter = new OpenCodeAdapter();

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        Assert.DoesNotContain("binaryPath", names);
    }

    [Fact]
    public void OpenCodeAdapter_custom_mcp_servers_field_is_a_textarea()
    {
        var field = Assert.Single(new OpenCodeAdapter().ConfigSchema, f => f.Name == "customMcpServersJson");
        Assert.Equal(ConfigFieldType.Textarea, field.Type);
        Assert.Equal("Custom MCP servers (JSON)", field.Label);
        Assert.False(field.Required);
        Assert.False(string.IsNullOrWhiteSpace(field.Description));
    }

    [Theory]
    [InlineData("claude-code", new[] { "customMcpServersJson", "extraArgs" })]
    [InlineData("copilot", new[] { "customMcpServersJson", "extraArgs" })]
    [InlineData("opencode", new[] { "customMcpServersJson", "extraArgs" })]
    [InlineData("pi", new[] { "extraArgs" })]
    public void Every_agent_adapter_offers_extra_cli_arguments_and_only_the_mcp_capable_ones_custom_mcp_servers(
        string type, string[] expectedNames)
    {
        CliAgentAdapterBase adapter = type switch
        {
            "claude-code" => new ClaudeCodeAdapter(),
            "copilot" => new CopilotAdapter(),
            "opencode" => new OpenCodeAdapter(),
            _ => new PiAdapter(),
        };

        Assert.Equal(expectedNames.Order(), adapter.ConfigSchema.Select(f => f.Name).Order());

        var field = Assert.Single(adapter.ConfigSchema, f => f.Name == "extraArgs");
        Assert.Equal(ConfigFieldType.Textarea, field.Type);
        Assert.Equal("Extra CLI arguments", field.Label);
        Assert.False(field.Required);
        Assert.Null(field.DefaultValue);
        Assert.False(string.IsNullOrWhiteSpace(field.Description));
    }

    [Fact]
    public void PiAdapter_schema_excludes_provider_level_fields()
    {
        var adapter = new PiAdapter();

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        Assert.DoesNotContain("binaryPath", names);
        Assert.DoesNotContain("provider", names);
        Assert.DoesNotContain("model", names);
        Assert.DoesNotContain("apiKey", names);
    }
}
