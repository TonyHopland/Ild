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
    public void OpenCodeAdapter_schema_exposes_custom_mcp_servers_field()
    {
        var adapter = new OpenCodeAdapter();

        var field = Assert.Single(adapter.ConfigSchema);
        Assert.Equal("customMcpServersJson", field.Name);
        Assert.Equal(ConfigFieldType.Textarea, field.Type);
        Assert.Equal("Custom MCP servers (JSON)", field.Label);
        Assert.False(field.Required);
        Assert.False(string.IsNullOrWhiteSpace(field.Description));
    }

    [Fact]
    public void ClaudeCodeAdapter_schema_exposes_custom_mcp_servers_field()
    {
        var adapter = new ClaudeCodeAdapter();

        var field = Assert.Single(adapter.ConfigSchema);
        Assert.Equal("customMcpServersJson", field.Name);
        Assert.Equal(ConfigFieldType.Textarea, field.Type);
    }

    [Fact]
    public void CopilotAdapter_schema_exposes_custom_mcp_servers_field()
    {
        var adapter = new CopilotAdapter();

        var field = Assert.Single(adapter.ConfigSchema);
        Assert.Equal("customMcpServersJson", field.Name);
        Assert.Equal(ConfigFieldType.Textarea, field.Type);
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

    [Fact]
    public void PiAdapter_schema_is_empty()
    {
        // Pi reaches the ILD MCP server through its extension bridge, but custom
        // MCP servers are not wired for pi, so it exposes no config field.
        var adapter = new PiAdapter();

        Assert.Empty(adapter.ConfigSchema);
    }
}
