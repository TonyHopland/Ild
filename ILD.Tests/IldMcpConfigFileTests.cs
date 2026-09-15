using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The MCP configs handed to the Copilot and Claude Code CLIs carry the ILD API
/// token, so they are written into the agent read root — readable by the agent
/// group, changeable by the orchestrator only — and the ones a dead process left
/// behind are swept at startup.
/// </summary>
public sealed class IldMcpConfigFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-mcp-config-root-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Copilots_config_is_readable_by_the_agent_group_only()
    {
        var provider = Provider("copilot");

        AssertAgentReadOnly(CopilotAdapter.TryWriteMcpConfig(provider, RunContext(), allowlist: Array.Empty<string>()));
    }

    [Fact]
    public void Claude_Codes_config_is_readable_by_the_agent_group_only()
    {
        var provider = Provider("claude-code");

        AssertAgentReadOnly(ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, RunContext(), toolAllowlist: new[] { "read" }));
    }

    [Fact]
    public void The_startup_sweep_removes_every_config_left_behind()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "ild-mcp-config")).FullName;
        File.WriteAllText(Path.Combine(directory, "ild-copilot-mcp-1.json"), "{\"token\":\"t\"}");
        File.WriteAllText(Path.Combine(directory, "ild-claude-mcp-2.json"), "{\"token\":\"t\"}");

        IldMcpServer.SweepConfigFiles(_root);
        IldMcpServer.SweepConfigFiles(Path.Combine(_root, "missing"));

        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    private static void AssertAgentReadOnly(string? path)
    {
        Assert.NotNull(path);
        try
        {
            Assert.Equal(Path.Combine(AgentIsolation.AgentReadRoot, "ild-mcp-config"), Path.GetDirectoryName(path));
            UnixOwnership.AssertOrchestratorOwned(Path.GetDirectoryName(path)!, UnixOwnership.AgentReadDirectory);
            UnixOwnership.AssertOrchestratorOwned(path!, UnixOwnership.AgentReadFile);
            using var doc = JsonDocument.Parse(File.ReadAllText(path!));
            Assert.True(doc.RootElement.GetProperty("mcpServers").TryGetProperty("docs", out _));
        }
        finally
        {
            File.Delete(path!);
        }
    }

    // ILD off, so only the custom server is written and no server DLL is needed.
    private static AiProvider Provider(string type) => new()
    {
        Name = $"{type}-test",
        Type = type,
        BaseUrl = string.Empty,
        Model = string.Empty,
        Config = JsonSerializer.Serialize(new { customMcpServersJson = """{ "docs": { "command": "npx" } }""" }),
    };

    private static LoopRunContext RunContext()
        => new(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);
}
