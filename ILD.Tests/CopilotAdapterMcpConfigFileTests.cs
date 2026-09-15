using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Copilot's MCP config carries the ILD API token and custom-server env for the
/// agent-uid CLI, so it must stay inside the agent group rather than land
/// world-readable in the temp dir.
/// </summary>
public class CopilotAdapterMcpConfigFileTests
{
    [Fact]
    public void The_config_is_written_into_shared_scratch_readable_by_the_agent_group_only()
    {
        var provider = new AiProvider
        {
            Name = "copilot-test",
            Type = "copilot",
            BaseUrl = string.Empty,
            Model = string.Empty,
            Config = JsonSerializer.Serialize(new { customMcpServersJson = """{ "docs": { "command": "npx" } }""" }),
        };
        var runContext = new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);

        // ILD off, so only the custom server is written and no server DLL is needed.
        var path = CopilotAdapter.TryWriteMcpConfig(provider, runContext, allowlist: Array.Empty<string>());

        Assert.NotNull(path);
        try
        {
            Assert.StartsWith(Path.GetFullPath(AgentIsolation.ScratchRoot), Path.GetFullPath(path!));
            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(path!);
                Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
                Assert.False(mode.HasFlag(UnixFileMode.GroupWrite), "the agent could rewrite the config");
                Assert.True(mode.HasFlag(UnixFileMode.GroupRead), "the agent-uid CLI must read the config");
            }
        }
        finally
        {
            File.Delete(path!);
        }
    }
}
