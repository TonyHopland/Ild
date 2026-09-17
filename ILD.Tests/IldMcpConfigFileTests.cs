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
    public void The_startup_sweep_removes_the_configs_an_earlier_process_left_and_keeps_this_ones()
    {
        var started = DateTime.UtcNow;
        var directory = Directory.CreateDirectory(Path.Combine(_root, "ild-mcp-config")).FullName;
        var copilot = Path.Combine(directory, "ild-copilot-mcp-1.json");
        var claude = Path.Combine(directory, "ild-claude-mcp-2.json");
        var current = Path.Combine(directory, "ild-copilot-mcp-3.json");
        foreach (var file in new[] { copilot, claude, current })
            File.WriteAllText(file, "{\"token\":\"t\"}");
        File.SetLastWriteTimeUtc(copilot, started.AddMinutes(-5));
        File.SetLastWriteTimeUtc(claude, started.AddHours(-1));
        File.SetLastWriteTimeUtc(current, started.AddSeconds(1));

        Assert.Equal(
            new[] { claude, copilot },
            IldMcpServer.StaleConfigFiles(_root, started).Order(StringComparer.Ordinal));
        Assert.Empty(IldMcpServer.StaleConfigFiles(Path.Combine(_root, "missing"), started));
    }

    [Fact]
    public void A_runs_configs_are_deleted_with_it_and_no_other_runs()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "ild-mcp-config")).FullName;
        var run = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mine = new[] { $"ild-copilot-mcp-{run:N}-1.json", $"ild-claude-mcp-{run:N}-2.json" };
        var theirs = Path.Combine(directory, $"ild-copilot-mcp-{other:N}-3.json");
        foreach (var name in mine)
            File.WriteAllText(Path.Combine(directory, name), "{\"token\":\"t\"}");
        File.WriteAllText(theirs, "{\"token\":\"t\"}");

        IldMcpServer.DeleteConfigFiles(_root, run);
        IldMcpServer.DeleteConfigFiles(Path.Combine(_root, "missing"), run);

        Assert.Equal(new[] { theirs }, Directory.GetFiles(directory));
    }

    [Fact]
    public void A_config_is_named_after_its_run()
    {
        var runContext = RunContext();

        var path = CopilotAdapter.TryWriteMcpConfig(Provider("copilot"), runContext, allowlist: Array.Empty<string>());

        Assert.NotNull(path);
        try
        {
            Assert.Contains($"-{runContext.LoopRunId:N}-", Path.GetFileName(path));
        }
        finally
        {
            File.Delete(path!);
        }
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
